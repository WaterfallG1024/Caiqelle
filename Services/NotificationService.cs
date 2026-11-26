using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Caiqelle.Services
{
    public class NotificationService
    {
        private Views.NotificationWindow? _notificationWindow;
        private DispatcherQueue? _uiDispatcher;

        public NotificationService()
        {
        }

        /// <summary>
        /// 初回通知時の遅延をなくすため、通知ウィンドウを事前に生成しておく
        /// </summary>
        public void Prewarm()
        {
            _uiDispatcher ??= DispatcherQueue.GetForCurrentThread();
            _notificationWindow ??= new Views.NotificationWindow();
        }

        public void ShowNotification(string message, string glyph = "\uE73E", int durationMs = 4000)
        {
            var app = Application.Current as App;
            var dispatcher = _uiDispatcher ?? _notificationWindow?.DispatcherQueue ?? app?.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return;

            if (!dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() =>
                {
                    _notificationWindow ??= new Views.NotificationWindow();
                    _notificationWindow.ShowMessage(message, glyph, durationMs);
                });
            }
            else
            {
                _notificationWindow ??= new Views.NotificationWindow();
                _notificationWindow.ShowMessage(message, glyph, durationMs);
            }
        }
    }
}
