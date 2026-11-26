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
        private readonly PinToolbarWindow _toolbarWindow;
        private readonly SoftwareBitmapSource _bitmapSource;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _frameLock = new(1, 1);

        private SoftwareBitmap? _reuseBitmapA;
        private SoftwareBitmap? _reuseBitmapB;
        private bool _useBufferA = true;
        private bool _isPaused = false;
        private bool _isClosed = false;
        private static readonly double[] AllowedScales = { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0 };
        private int _scaleIndex = 1; // 初期倍率: 1x (枠内フィット)
        private bool _isPanning = false;
        private Windows.Foundation.Point _lastPanPoint;

        public LivePinWindow(Rectangle targetBounds, ScreenshotService screenshotService)
        {
            _targetBounds = targetBounds;
            _screenshotService = screenshotService;

            this.InitializeComponent();
            if (((App)Application.Current).Services.GetService(typeof(ThemeService)) is ThemeService themeService)
            {
                themeService.RegisterWindow(this, RootGrid);
            }

            _hwnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            ExtendsContentIntoTitleBar = true;

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
            ApplyScaleIndex(1);

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

            _toolbarWindow = new PinToolbarWindow(isLivePin: true);
            _toolbarWindow.PauseResumeRequested += TogglePause;
            _toolbarWindow.ZoomInRequested += () => StepScaleIndex(1);
            _toolbarWindow.ZoomOutRequested += () => StepScaleIndex(-1);
            _toolbarWindow.ScaleResetRequested += () => ApplyScaleIndex((_scaleIndex + 1) % AllowedScales.Length);
            _toolbarWindow.CopyRequested += () => CopyBtn_Click(this, new RoutedEventArgs());
            _toolbarWindow.SaveRequested += () => SaveBtn_Click(this, new RoutedEventArgs());
            _toolbarWindow.CloseRequested += () => CloseBtn_Click(this, new RoutedEventArgs());
            _toolbarWindow.MoveDragRequested += () =>
            {
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            };
            _toolbarWindow.SyncScaleText(AllowedScales[_scaleIndex]);

            _appWindow.Changed += (_, args) =>
            {
                if ((args.DidPositionChange || args.DidSizeChange) && !_isClosed)
                {
                    _toolbarWindow.UpdatePosition(new Windows.Graphics.RectInt32(
                        _appWindow.Position.X, _appWindow.Position.Y,
                        _appWindow.Size.Width, _appWindow.Size.Height));
                }
            };

            this.Closed += LivePinWindow_Closed;
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

            if (!await _frameLock.WaitAsync(0, ct)) return;
            try
            {
                if (ct.IsCancellationRequested || _isClosed) return;

                // 同一インスタンスの再利用では SoftwareBitmapSource が更新を検知しない場合があるためダブルバッファで交互に描画
                var targetBuffer = _useBufferA ? _reuseBitmapA : _reuseBitmapB;
                var captured = await Task.Run(() => _screenshotService.CaptureRegionToSoftwareBitmap(_targetBounds, targetBuffer), ct);
                if (captured == null) return;

                if (ct.IsCancellationRequested || _isClosed)
                {
                    if (!ReferenceEquals(captured, _reuseBitmapA) && !ReferenceEquals(captured, _reuseBitmapB))
                    {
                        captured.Dispose();
                    }
                    return;
                }

                if (_useBufferA)
                    _reuseBitmapA = captured;
                else
                    _reuseBitmapB = captured;
                _useBufferA = !_useBufferA;

                await _bitmapSource.SetBitmapAsync(captured);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LivePinWindow] Frame update error: {ex.Message}");
            }
            finally
            {
                if (_isClosed)
                {
                    _reuseBitmapA?.Dispose();
                    _reuseBitmapA = null;
                    _reuseBitmapB?.Dispose();
                    _reuseBitmapB = null;
                    _bitmapSource.Dispose();
                }
                _frameLock.Release();
            }
        }

        private void TogglePause()
        {
            if (_isClosed) return;
            _isPaused = !_isPaused;
            _toolbarWindow?.SyncPauseState(_isPaused);
            if (_isPaused)
            {
                StatusText.Text = "PAUSE";
                StatusDot.Fill = new SolidColorBrush(Colors.Gray);
            }
            else
            {
                StatusText.Text = "LIVE";
                if (Application.Current.Resources.TryGetValue("FluentPrimaryBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush primaryBrush)
                {
                    StatusDot.Fill = primaryBrush;
                }
                try { _ = CaptureAndRenderFrameAsync(_cts.Token); } catch (ObjectDisposedException) { }
            }
        }

        private void LivePinWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosed = true;
            _toolbarWindow?.SafeClose();
            _cts.Cancel();
            if (_frameLock.Wait(0))
            {
                try
                {
                    _reuseBitmapA?.Dispose();
                    _reuseBitmapA = null;
                    _reuseBitmapB?.Dispose();
                    _reuseBitmapB = null;
                    _bitmapSource.Dispose();
                }
                finally
                {
                    _frameLock.Release();
                }
            }
            _cts.Dispose();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _toolbarWindow?.NotifyParentPointerEntered(new Windows.Graphics.RectInt32(
                _appWindow.Position.X, _appWindow.Position.Y,
                _appWindow.Size.Width, _appWindow.Size.Height));
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _toolbarWindow?.NotifyParentPointerExited();
        }

        private void ViewportGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isClosed) return;
            ViewportGrid.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
            ClampPanOffset(AllowedScales[_scaleIndex]);
        }

        private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ViewportGrid);
            int delta = point.Properties.MouseWheelDelta;
            if (delta == 0) return;

            StepScaleIndex(delta > 0 ? 1 : -1, point.Position);
            e.Handled = true;
        }

        private void StepScaleIndex(int deltaIndex, Windows.Foundation.Point? anchorPoint = null)
        {
            int nextIndex = Math.Clamp(_scaleIndex + deltaIndex, 0, AllowedScales.Length - 1);
            ApplyScaleIndex(nextIndex, anchorPoint);
        }

        private void ApplyScaleIndex(int index, Windows.Foundation.Point? anchorPoint = null)
        {
            double oldScale = ImageTransform.ScaleX > 0 ? ImageTransform.ScaleX : 1.0;
            _scaleIndex = Math.Clamp(index, 0, AllowedScales.Length - 1);
            double newScale = AllowedScales[_scaleIndex];

            // 拡大時かつ基準座標（カーソル位置）がある場合は、その位置を中心にズーム
            if (newScale <= 1.0)
            {
                ImageTransform.TranslateX = 0;
                ImageTransform.TranslateY = 0;
            }
            else if (anchorPoint.HasValue && ViewportGrid.ActualWidth > 0 && ViewportGrid.ActualHeight > 0)
            {
                double cx = ViewportGrid.ActualWidth / 2.0;
                double cy = ViewportGrid.ActualHeight / 2.0;
                double dx = anchorPoint.Value.X - cx;
                double dy = anchorPoint.Value.Y - cy;

                ImageTransform.TranslateX = dx - newScale * (dx - ImageTransform.TranslateX) / oldScale;
                ImageTransform.TranslateY = dy - newScale * (dy - ImageTransform.TranslateY) / oldScale;
            }

            ImageTransform.ScaleX = newScale;
            ImageTransform.ScaleY = newScale;
            ClampPanOffset(newScale);
            UpdateScaleText();
        }

        private void ClampPanOffset(double scale)
        {
            if (scale <= 1.0)
            {
                ImageTransform.TranslateX = 0;
                ImageTransform.TranslateY = 0;
                return;
            }

            double viewW = ViewportGrid.ActualWidth;
            double viewH = ViewportGrid.ActualHeight;
            double imgW = LiveImage.ActualWidth > 0 ? LiveImage.ActualWidth : viewW;
            double imgH = LiveImage.ActualHeight > 0 ? LiveImage.ActualHeight : viewH;

            double maxPanX = Math.Max(0, (imgW * scale - viewW) / 2.0);
            double maxPanY = Math.Max(0, (imgH * scale - viewH) / 2.0);

            ImageTransform.TranslateX = Math.Clamp(ImageTransform.TranslateX, -maxPanX, maxPanX);
            ImageTransform.TranslateY = Math.Clamp(ImageTransform.TranslateY, -maxPanY, maxPanY);
        }

        private void UpdateScaleText()
        {
            double scale = AllowedScales[_scaleIndex];
            _toolbarWindow?.SyncScaleText(scale);
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => StepScaleIndex(-1);

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => StepScaleIndex(1);

        private void ScaleResetBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1x -> 2x -> 3x -> 1x の順に循環切り替え
            int nextIndex = _scaleIndex switch
            {
                1 => 3, // 1x -> 2x
                3 => 4, // 2x -> 3x
                _ => 1  // その他 -> 1x
            };
            ApplyScaleIndex(nextIndex);
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
                ApplyScaleIndex(1); // 1x
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Number2 || e.Key == VirtualKey.NumberPad2)
            {
                ApplyScaleIndex(3); // 2x
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Number3 || e.Key == VirtualKey.NumberPad3)
            {
                ApplyScaleIndex(4); // 3x
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
            if (e.OriginalSource is not (Microsoft.UI.Xaml.Controls.Image or Grid)) return;

            var point = e.GetCurrentPoint(RootGrid);
            if (point.Properties.IsLeftButtonPressed && AllowedScales[_scaleIndex] > 1.0)
            {
                // 拡大表示中は左ボタンドラッグで表示位置をパン移動
                _isPanning = true;
                _lastPanPoint = point.Position;
                RootGrid.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanning) return;

            var point = e.GetCurrentPoint(RootGrid);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _isPanning = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
                return;
            }

            double dx = point.Position.X - _lastPanPoint.X;
            double dy = point.Position.Y - _lastPanPoint.Y;
            _lastPanPoint = point.Position;

            ImageTransform.TranslateX += dx;
            ImageTransform.TranslateY += dy;
            ClampPanOffset(AllowedScales[_scaleIndex]);
            e.Handled = true;
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanning) return;

            _isPanning = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }
}
