using Caiqelle.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace Caiqelle.Views
{
    public sealed partial class LivePinWindow : Window
    {
        private readonly Rectangle _targetBounds;
        private readonly ScreenshotService _screenshotService;
        private readonly AppWindow _appWindow;
        private readonly IntPtr _hwnd;
        private readonly SoftwareBitmapSource _bitmapSource;
        private readonly CancellationTokenSource _cts = new();

        private SoftwareBitmap? _reuseBitmapA;
        private SoftwareBitmap? _reuseBitmapB;
        private bool _useBufferA = true;
        private bool _isPaused = false;
        private bool _isClosed = false;
        private static readonly double[] AllowedScales = { 1.0, 2.0, 3.0 };
        private int _scaleIndex = 1; // 初期倍率: 2x

        public LivePinWindow(Rectangle targetBounds, ScreenshotService screenshotService)
        {
            _targetBounds = targetBounds;
            _screenshotService = screenshotService;

            this.InitializeComponent();

            _hwnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(RootGrid);

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = true;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(true, false);
            }

            // 自身をキャプチャ対象から除外（元領域の上に重ねても無限映り込みを防止）
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

            // 初期拡大表示（2倍拡大、画面内に収まるようアスペクト比維持でクランプ）
            double aspect = (double)Math.Max(1, _targetBounds.Width) / Math.Max(1, _targetBounds.Height);
            int initW = _targetBounds.Width * 2;
            int initH = _targetBounds.Height * 2;

            if (initW < 240)
            {
                initW = 240;
                initH = (int)Math.Round(initW / aspect);
            }
            if (initH < 140)
            {
                initH = 140;
                initW = (int)Math.Round(initH * aspect);
            }
            if (initW > 1200)
            {
                initW = 1200;
                initH = (int)Math.Round(initW / aspect);
            }
            if (initH > 1000)
            {
                initH = 1000;
                initW = (int)Math.Round(initH * aspect);
            }

            _appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(160, initW), Math.Max(120, initH)));
            SyncNearestScaleIndex(Math.Max(160, initW));
            UpdateScaleText();

            // 対象領域の近くに配置（ディスプレイ作業領域内に収める）
            var displayArea = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(_targetBounds.X + _targetBounds.Width / 2, _targetBounds.Y + _targetBounds.Height / 2),
                DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                int posX = Math.Clamp(_targetBounds.X + (_targetBounds.Width - initW) / 2, work.X, work.X + Math.Max(0, work.Width - initW));
                int posY = Math.Clamp(_targetBounds.Y + (_targetBounds.Height - initH) / 2, work.Y, work.Y + Math.Max(0, work.Height - initH));
                _appWindow.Move(new Windows.Graphics.PointInt32(posX, posY));
            }

            _bitmapSource = new SoftwareBitmapSource();
            LiveImage.Source = _bitmapSource;

            this.Closed += LivePinWindow_Closed;
            this.SizeChanged += (s, e) =>
            {
                if (_isClosed) return;
                SyncNearestScaleIndex(_appWindow.Size.Width);
                UpdateScaleText();
            };
            _ = RunLiveLoopAsync(_cts.Token);
        }

        private async Task RunLiveLoopAsync(CancellationToken ct)
        {
            // 初回フレームは即時取得して表示し、その後約20fps (50ms間隔) で更新
            await CaptureAndRenderFrameAsync(ct);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (_isPaused || _isClosed) continue;
                    await CaptureAndRenderFrameAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task CaptureAndRenderFrameAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested || _isClosed) return;

            try
            {
                // 同一インスタンスの再利用では SoftwareBitmapSource が更新を検知しない場合があるためダブルバッファで交互に描画
                var targetBuffer = _useBufferA ? _reuseBitmapA : _reuseBitmapB;
                var captured = await Task.Run(() => _screenshotService.CaptureRegionToSoftwareBitmap(_targetBounds, targetBuffer), ct);
                if (captured == null || ct.IsCancellationRequested || _isClosed) return;

                if (_useBufferA)
                    _reuseBitmapA = captured;
                else
                    _reuseBitmapB = captured;
                _useBufferA = !_useBufferA;

                await _bitmapSource.SetBitmapAsync(captured);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LivePinWindow] Frame update error: {ex.Message}");
            }
        }

        private void TogglePause()
        {
            _isPaused = !_isPaused;
            if (_isPaused)
            {
                PauseResumeIcon.Glyph = "\uE768"; // Play
                StatusText.Text = "PAUSE";
                StatusDot.Fill = new SolidColorBrush(Colors.Gray);
            }
            else
            {
                PauseResumeIcon.Glyph = "\uE769"; // Pause
                StatusText.Text = "LIVE";
                if (Application.Current.Resources.TryGetValue("M3PrimaryBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush primaryBrush)
                {
                    StatusDot.Fill = primaryBrush;
                }
                _ = CaptureAndRenderFrameAsync(_cts.Token);
            }
        }

        private void LivePinWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosed = true;
            _cts.Cancel();
            _cts.Dispose();
            _reuseBitmapA?.Dispose();
            _reuseBitmapA = null;
            _reuseBitmapB?.Dispose();
            _reuseBitmapB = null;
            _bitmapSource.Dispose();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            OverlayPanel.Opacity = 1.0;
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            OverlayPanel.Opacity = 0.0;
        }

        private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
            if (delta == 0) return;

            StepScaleIndex(delta > 0 ? 1 : -1);
        }

        private void StepScaleIndex(int deltaIndex)
        {
            int nextIndex = Math.Clamp(_scaleIndex + deltaIndex, 0, AllowedScales.Length - 1);
            ApplyScaleIndex(nextIndex);
        }

        private void ApplyScaleIndex(int index)
        {
            _scaleIndex = Math.Clamp(index, 0, AllowedScales.Length - 1);
            double targetScale = AllowedScales[_scaleIndex];
            double aspect = (double)Math.Max(1, _targetBounds.Width) / Math.Max(1, _targetBounds.Height);

            int newW = (int)Math.Round(_targetBounds.Width * targetScale);
            int newH = (int)Math.Round(_targetBounds.Height * targetScale);

            // アスペクト比を維持したまま最小・最大サイズ内にクランプ
            if (newW < 160)
            {
                newW = 160;
                newH = (int)Math.Round(newW / aspect);
            }
            if (newH < 120)
            {
                newH = 120;
                newW = (int)Math.Round(newH * aspect);
            }
            if (newW > 1800)
            {
                newW = 1800;
                newH = (int)Math.Round(newW / aspect);
            }
            if (newH > 1400)
            {
                newH = 1400;
                newW = (int)Math.Round(newH * aspect);
            }

            _appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(160, newW), Math.Max(120, newH)));
            UpdateScaleText();
        }

        private void SyncNearestScaleIndex(int currentWidth)
        {
            double ratio = (double)currentWidth / Math.Max(1, _targetBounds.Width);
            int bestIdx = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < AllowedScales.Length; i++)
            {
                double diff = Math.Abs(ratio - AllowedScales[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestIdx = i;
                }
            }
            _scaleIndex = bestIdx;
        }

        private void UpdateScaleText()
        {
            ScaleText.Text = $"{(int)AllowedScales[_scaleIndex]}x";
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => StepScaleIndex(-1);

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => StepScaleIndex(1);

        private void ScaleResetBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1x -> 2x -> 3x -> 1x の順に循環切り替え
            ApplyScaleIndex((_scaleIndex + 1) % AllowedScales.Length);
        }

        private void PauseResumeBtn_Click(object sender, RoutedEventArgs e) => TogglePause();

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => this.Close();

        private async void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                byte[] bytes = await _screenshotService.CaptureRegionToBytesAsync(_targetBounds);
                if (bytes == null || bytes.Length == 0) return;

                var clipboardService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ClipboardService>(((App)Application.Current).Services);
                await clipboardService.CopyImageAsync(bytes, showNotification: true, DispatcherQueue);
            }
            catch { }
        }

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                byte[] bytes = await _screenshotService.CaptureRegionToBytesAsync(_targetBounds);
                if (bytes != null && bytes.Length > 0)
                {
                    var app = (App)Application.Current;
                    if (app.CaptureManager != null)
                    {
                        await app.CaptureManager.ShowFolderSelectionAsync(bytes);
                    }
                    else
                    {
                        var folderWindow = new FolderSelectionWindow(app);
                        await folderWindow.ShowAsync(bytes);
                    }
                }
            }
            catch { }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                this.Close();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Space)
            {
                TogglePause();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Add || (int)e.Key == 187)
            {
                StepScaleIndex(1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Subtract || (int)e.Key == 189)
            {
                StepScaleIndex(-1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Number1 || e.Key == VirtualKey.NumberPad1)
            {
                ApplyScaleIndex(0);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Number2 || e.Key == VirtualKey.NumberPad2)
            {
                ApplyScaleIndex(1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Number3 || e.Key == VirtualKey.NumberPad3)
            {
                ApplyScaleIndex(2);
                e.Handled = true;
            }
            else
            {
                var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                bool isCtrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                if (isCtrl && e.Key == VirtualKey.C)
                {
                    CopyBtn_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (isCtrl && e.Key == VirtualKey.S)
                {
                    SaveBtn_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Image || e.OriginalSource is Grid)
            {
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
    }
}
