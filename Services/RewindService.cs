using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Caiqelle.Services
{
    public class RewindFrame
    {
        public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
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
        private int _gcCounter = 0;

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

            System.Diagnostics.Debug.WriteLine($"[RewindService] Stopped. BufferedFrames={_frames.Count}");

            Microsoft.UI.Xaml.Application.Current.Resources.DispatcherQueue?.TryEnqueue(() => 
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        public void Restart()
        {
            Stop();
            Start();
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

                byte[] bytes = _screenshotService.CaptureRegionToJpegBytes(bounds, targetHeight, quality);

                if (bytes != null && bytes.Length > 0 && _isRunning && !_isPaused)
                {
                    var frame = new RewindFrame
                    {
                        JpegBytes = bytes,
                        Timestamp = DateTime.Now
                    };

                    _frames.Enqueue(frame);

                    // 最大フレーム数を超えたら古いフレームを削除
                    int maxFrames = Math.Max(1, _settingsManager.RewindDurationSeconds);

                    while (_frames.Count > maxFrames)
                    {
                        _frames.TryDequeue(out _);
                        _gcCounter++;
                        
                        if (_gcCounter > 10)
                        {
                            GC.Collect(0, GCCollectionMode.Optimized);
                            _gcCounter = 0;
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"[RewindService] Frame captured. Total={_frames.Count}/{maxFrames}, Size={bytes.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RewindService] Capture Error: {ex.GetType().Name}: {ex.Message}");
            }

            // 次のキャプチャをスケジュール（キャプチャの成否に関わらず必ず実行）
            ScheduleNextCapture(1000);
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
