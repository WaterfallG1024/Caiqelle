using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Caiqelle.Services
{
    /// <summary>
    /// 画像データのクリップボードコピーと完了通知を共通で提供するサービス
    /// </summary>
    public class ClipboardService
    {
        private readonly NotificationService _notificationService;

        public ClipboardService(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// 画像バイト列をクリップボードへコピーする（必要に応じて完了通知を表示）
        /// </summary>
        public Task<bool> CopyImageAsync(byte[] imageBytes, bool showNotification = true, DispatcherQueue? dispatcherQueue = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return Task.FromResult(false);

            var dispatcher = dispatcherQueue
                ?? ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.DispatcherQueue
                ?? DispatcherQueue.GetForCurrentThread();

            if (dispatcher == null)
                return CopyCoreAsync(imageBytes, showNotification);

            if (dispatcher.HasThreadAccess)
                return CopyCoreAsync(imageBytes, showNotification);

            var tcs = new TaskCompletionSource<bool>();
            dispatcher.TryEnqueue(async () =>
            {
                bool ok = await CopyCoreAsync(imageBytes, showNotification);
                tcs.TrySetResult(ok);
            });
            return tcs.Task;
        }

        private async Task<bool> CopyCoreAsync(byte[] imageBytes, bool showNotification)
        {
            try
            {
                var dataPackage = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy
                };

                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(imageBytes);
                    await writer.StoreAsync();
                }
                stream.Seek(0);

                var rsr = RandomAccessStreamReference.CreateFromStream(stream);
                dataPackage.SetBitmap(rsr);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();

                if (showNotification)
                {
                    _notificationService.ShowNotification("クリップボードにコピーしました", "\uE8C8");
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Copy error: {ex.Message}");
                return false;
            }
        }
    }
}
