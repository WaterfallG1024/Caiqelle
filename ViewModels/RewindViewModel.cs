using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Caiqelle.Services;
using Caiqelle.Views;

namespace Caiqelle.ViewModels
{
    /// <summary>
    /// RewindWindow のフレーム一覧・ナビゲーション・プレビュー表示を管理する ViewModel
    /// </summary>
    public partial class RewindViewModel : ObservableObject
    {
        private readonly RewindService _rewindService;
        private readonly EditorWindow _editorWindow;
        private RewindFrame[] _frames = Array.Empty<RewindFrame>();
        private int _currentIndex;

        /// <summary>
        /// ウィンドウを閉じるためのコールバック
        /// </summary>
        public Action? RequestClose { get; set; }

        [ObservableProperty]
        public partial string IndexText { get; set; } = "0/0";

        [ObservableProperty]
        public partial string TimestampText { get; set; } = "矢印キーまたはマウスホイールでフレーム移動 / 画像タップで編集";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OlderButtonOpacity))]
        public partial bool CanGoOlder { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NewerButtonOpacity))]
        public partial bool CanGoNewer { get; set; }

        public double OlderButtonOpacity => CanGoOlder ? 1.0 : 0.35;
        public double NewerButtonOpacity => CanGoNewer ? 1.0 : 0.35;

        [ObservableProperty]
        public partial bool HasFrames { get; set; }

        [ObservableProperty]
        public partial BitmapImage? PreviewImageSource { get; set; }

        public RewindViewModel(RewindService rewindService, EditorWindow editorWindow)
        {
            _rewindService = rewindService;
            _editorWindow = editorWindow;
        }

        /// <summary>
        /// バッファ内のフレームを読み込み、最新フレームを表示する
        /// </summary>
        public void LoadFrames()
        {
            _frames = _rewindService.GetBufferedFrames();
            if (_frames.Length > 0)
            {
                _frames = _frames.OrderByDescending(f => f.Timestamp).ToArray();
                _currentIndex = 0;
                HasFrames = true;
                _ = UpdatePreviewAsync();
            }
            else
            {
                HasFrames = false;
                IndexText = "0/0";
                TimestampText = "録画されたフレームがありません";
                CanGoOlder = false;
                CanGoNewer = false;
                PreviewImageSource = null;
            }
        }

        /// <summary>
        /// フレーム配列を解放する
        /// </summary>
        public void ClearFrames()
        {
            _frames = Array.Empty<RewindFrame>();
            PreviewImageSource = null;
        }

        [RelayCommand]
        private void GoOlder() => Navigate(1);

        [RelayCommand]
        private void GoNewer() => Navigate(-1);

        /// <summary>
        /// 指定したオフセット分だけフレームを移動する
        /// </summary>
        public void Navigate(int offset)
        {
            if (_frames.Length == 0) return;

            _currentIndex = Math.Clamp(_currentIndex + offset, 0, _frames.Length - 1);
            _ = UpdatePreviewAsync();
        }

        private async Task<byte[]?> GetCurrentFrameBytesAsync()
        {
            if (_frames.Length == 0) return null;

            var frame = _frames[_currentIndex];
            byte[] imageBytes = frame.IsTiled
                ? await Task.Run(() => _rewindService.GetFrameImageBytes(frame))
                : frame.JpegBytes;

            return imageBytes.Length > 0 ? imageBytes : null;
        }

        [RelayCommand]
        private async Task OpenInEditorAsync()
        {
            byte[]? imageBytes = await GetCurrentFrameBytesAsync();
            if (imageBytes == null) return;

            await _editorWindow.LoadImageAsync(imageBytes);
            _editorWindow.Activate();
            RequestClose?.Invoke();
        }

        [RelayCommand]
        private async Task CopyToClipboardAsync()
        {
            byte[]? imageBytes = await GetCurrentFrameBytesAsync();
            if (imageBytes == null) return;

            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(imageBytes.AsBuffer());
                stream.Seek(0);
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();

                var notificationService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<NotificationService>(((App)Microsoft.UI.Xaml.Application.Current).Services);
                notificationService.ShowNotification("クリップボードにコピーしました", "\uE8C8");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RewindViewModel] Copy error: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task SaveAsync()
        {
            byte[]? imageBytes = await GetCurrentFrameBytesAsync();
            if (imageBytes == null) return;

            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var folderWindow = new Views.FolderSelectionWindow(app);
            await folderWindow.ShowAsync(imageBytes);
        }

        [RelayCommand]
        private void Close()
        {
            RequestClose?.Invoke();
        }

        private async Task UpdatePreviewAsync()
        {
            if (_frames.Length == 0) return;

            var frame = _frames[_currentIndex];
            // 1秒1フレームで録画しているため、インデックスがそのまま経過秒数に対応し数字飛びを防ぐ
            int elapsedSeconds = _currentIndex;
            IndexText = $"{_currentIndex + 1}/{_frames.Length}({MainViewModel.FormatDuration(elapsedSeconds)}前)";
            TimestampText = $"記録時刻: {frame.Timestamp:yyyy/MM/dd HH:mm:ss}  •  矢印キー / ホイールで移動";

            CanGoOlder = _currentIndex < _frames.Length - 1;
            CanGoNewer = _currentIndex > 0;

            try
            {
                byte[] imageBytes = frame.IsTiled
                    ? await Task.Run(() => _rewindService.GetFrameImageBytes(frame))
                    : frame.JpegBytes;

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(imageBytes.AsBuffer());
                    stream.Seek(0);

                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    PreviewImageSource = bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RewindViewModel] Image render error: {ex.Message}");
            }
        }
    }
}
