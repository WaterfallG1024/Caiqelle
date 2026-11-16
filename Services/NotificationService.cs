using Microsoft.UI.Xaml;

namespace Caiqelle.Services
{
    public class NotificationService
    {
        private Views.NotificationWindow? _notificationWindow;

        public NotificationService()
        {
        }

        public void ShowNotification(string message, string glyph = "\uE73E", int durationMs = 4000)
        {
            if (_notificationWindow == null)
            {
                _notificationWindow = new Views.NotificationWindow();
            }

            var app = (App)Application.Current;
            var windowToUse = app.MainWindow;
            var dispatcher = windowToUse?.DispatcherQueue;

            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(() =>
                {
                    _notificationWindow.ShowMessage(message, glyph, durationMs);
                });
            }
            else
            {
                // Fallback if main window is null
                _notificationWindow.ShowMessage(message, glyph, durationMs);
            }
        }
    }
}
