using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Caiqelle.Services
{
    public class RewindFrame
    {
        public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
        public byte[]?[]? Tiles { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsTiled => Tiles != null;
        public DateTime Timestamp { get; set; }
    }

    public class RewindService : IDisposable
    {
        private readonly ScreenshotService _screenshotService;
        private readonly SettingsManager _settingsManager;
        
        private Timer? _timer;
        private readonly ConcurrentQueue<RewindFrame> _frames = new();
        private bool _isRunning = false;
        private volatile bool _isPaused = false;

        // 16分割タイル差分記録用の直前フレーム状態
        private byte[]? _prevRawPixels;
        private int _prevWidth;
        private int _prevHeight;
        private byte[]?[]? _prevTiles;

        public event EventHandler? StateChanged;
        public bool IsRunning => _isRunning;
        public bool IsPaused => _isPaused;
        public int FrameCount => _frames.Count;

        public RewindService(ScreenshotService screenshotService, SettingsManager settingsManager)
        {
            _screenshotService = screenshotService;
            _settingsManager = settingsManager;
        }

        public void Pause()
        {
            _isPaused = true;
        }

        public void Resume()
        {
            _isPaused = false;
            // 一時停止解除後に次のキャプチャをスケジュール
            ScheduleNextCapture(500);
        }

        public void Start(bool clearBuffer = true)
        {
            if (_isRunning) return;
            
            if (!_settingsManager.IsRewindEnabled) return;

            if (clearBuffer)
            {
                while (_frames.TryDequeue(out _)) { }
                ClearDiffCache();
            }

            _isPaused = false;
            _isRunning = true;

            // ワンショットタイマーパターン: 1回だけ発火し、完了後に次をスケジュール
            _timer = new Timer(CaptureTick, null, 500, Timeout.Infinite);

            System.Diagnostics.Debug.WriteLine("[RewindService] Started.");

            Microsoft.UI.Xaml.Application.Current.Resources.DispatcherQueue?.TryEnqueue(() => 
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _isPaused = false;
            
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
            ClearDiffCache();

            System.Diagnostics.Debug.WriteLine($"[RewindService] Stopped. BufferedFrames={_frames.Count}");

            Microsoft.UI.Xaml.Application.Current.Resources.DispatcherQueue?.TryEnqueue(() => 
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        public void Restart(bool clearBuffer = true)
        {
            Stop();
            Start(clearBuffer);
        }

        private void ClearDiffCache()
        {
            _prevRawPixels = null;
            _prevTiles = null;
            _prevWidth = 0;
            _prevHeight = 0;
        }

        /// <summary>
        /// フレームから1枚の画像データ（JPEGバイト列）を取得・合成する
        /// </summary>
        public byte[] GetFrameImageBytes(RewindFrame frame)
        {
            if (frame.IsTiled && frame.Tiles != null)
            {
                return _screenshotService.ComposeTilesToJpegBytes(frame.Tiles, frame.Width, frame.Height);
            }
            return frame.JpegBytes;
        }

        /// <summary>
        /// 次のキャプチャをスケジュールする（ワンショットタイマー）
        /// </summary>
        private void ScheduleNextCapture(int delayMs = 1000)
        {
            if (!_isRunning) return;
            try
            {
                _timer?.Change(delayMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
        }

        private void CaptureTick(object? state)
        {
            // 一時停止中ならスキップ（Resume時に再スケジュールされる）
            if (!_isRunning) return;
            if (_isPaused)
            {
                // 一時停止中でも定期的にチェック（Resume漏れ対策）
                ScheduleNextCapture(2000);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var bounds = _screenshotService.GetVirtualScreenBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[RewindService] Invalid screen bounds, retrying...");
                    ScheduleNextCapture(2000);
                    return;
                }
                
                int targetHeight = bounds.Height;
                long quality = 75L;
                
                // 画質設定を取得
                switch (_settingsManager.RewindQuality)
                {
                    case "720p": targetHeight = 720; quality = 60L; break;
                    case "FulHD": targetHeight = 1080; quality = 75L; break;
                    case "2K": targetHeight = 1440; quality = 80L; break;
                    case "4K": targetHeight = 2160; quality = 85L; break;
                }
                
                // アップスケールを防止
                if (targetHeight > bounds.Height) targetHeight = bounds.Height;

                RewindFrame? frame = null;

                if (_settingsManager.IsRewindTileCompressionEnabled)
                {
                    var tiles = _screenshotService.CaptureTiledDiffJpegBytes(
                        bounds,
                        targetHeight,
                        quality,
                        ref _prevRawPixels,
                        ref _prevWidth,
                        ref _prevHeight,
                        _prevTiles,
                        out int outW,
                        out int outH,
                        out int changedCount);

                    if (tiles.Length == ScreenshotService.RewindTileCount && _isRunning && !_isPaused)
                    {
                        _prevTiles = tiles;
                        frame = new RewindFrame
                        {
                            Tiles = tiles,
                            Width = outW,
                            Height = outH,
                            Timestamp = DateTime.Now
                        };
                        System.Diagnostics.Debug.WriteLine($"[RewindService] Tiled frame captured. Changed={changedCount}/16");
                    }
                }
                else
                {
                    byte[] bytes = _screenshotService.CaptureRegionToJpegBytes(bounds, targetHeight, quality);
                    if (bytes != null && bytes.Length > 0 && _isRunning && !_isPaused)
                    {
                        frame = new RewindFrame
                        {
                            JpegBytes = bytes,
                            Timestamp = DateTime.Now
                        };
                    }
                }

                if (frame != null)
                {
                    _frames.Enqueue(frame);

                    // 最大フレーム数を超えたら古いフレームを削除
                    int maxFrames = Math.Max(1, _settingsManager.RewindDurationSeconds);

                    while (_frames.Count > maxFrames)
                    {
                        _frames.TryDequeue(out _);
                    }

                    System.Diagnostics.Debug.WriteLine($"[RewindService] Frame enqueued. Total={_frames.Count}/{maxFrames}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RewindService] Capture Error: {ex.GetType().Name}: {ex.Message}");
            }

            // 処理時間分を差し引いて平均1000ms間隔を維持する
            int nextDelay = Math.Max(50, 1000 - (int)sw.ElapsedMilliseconds);
            ScheduleNextCapture(nextDelay);
        }

        public RewindFrame[] GetBufferedFrames()
        {
            var result = _frames.ToArray();
            System.Diagnostics.Debug.WriteLine($"[RewindService] GetBufferedFrames() returning {result.Length} frames.");
            return result;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
