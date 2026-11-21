using System;
using System.Drawing;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Caiqelle.Views;
using System.IO;

namespace Caiqelle.Services
{
    public class CaptureManager
    {
        private readonly ScreenshotService _screenshotService;
        private EditorWindow? _editorWindow;
        private OverlayWindow? _overlayWindow;
        private WindowSelectionOverlay? _windowSelectionOverlay;
        private FolderSelectionWindow? _folderSelectionWindow;

        public CaptureManager(ScreenshotService screenshotService, EditorWindow? editorWindow = null)
        {
            _screenshotService = screenshotService;
            _editorWindow = editorWindow;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private OverlayWindow GetOverlayWindow() => _overlayWindow ??= new OverlayWindow();
        private WindowSelectionOverlay GetWindowSelectionOverlay() => _windowSelectionOverlay ??= new WindowSelectionOverlay();
        private FolderSelectionWindow GetFolderSelectionWindow() => _folderSelectionWindow ??= new FolderSelectionWindow((App)Microsoft.UI.Xaml.Application.Current);
        public EditorWindow GetEditorWindow() => _editorWindow ??= new EditorWindow();

        /// <summary>
        /// キャッシュ済みのフォルダ選択ウィンドウを再利用して表示する
        /// </summary>
        public Task<bool> ShowFolderSelectionAsync(byte[] imageBytes)
        {
            return GetFolderSelectionWindow().ShowAsync(imageBytes);
        }

        /// <summary>
        /// 初回キャプチャ時の遅延をなくすため、オーバーレイ・保存ウィンドウを事前に生成しておく
        /// </summary>
        public void Prewarm()
        {
            _ = GetOverlayWindow();
            _ = GetWindowSelectionOverlay();
            _ = GetFolderSelectionWindow();
        }

        /// <summary>
        /// UI スレッドをブロックしないよう、1 ウィンドウずつ段階的に事前生成する
        /// </summary>
        public void PrewarmStep(int step)
        {
            switch (step)
            {
                case 0: _ = GetOverlayWindow(); break;
                case 1: _ = GetWindowSelectionOverlay(); break;
                case 2: _ = GetFolderSelectionWindow(); break;
            }
        }

        public void CancelCapture()
        {
            _overlayWindow?.Cancel();
            _windowSelectionOverlay?.Cancel();
        }

        public async Task StartCaptureAsync(CapturedKey key)
        {
            var rewindService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<RewindService>(((App)Application.Current).Services);
            rewindService?.Pause();

            try
            {
                // FloatingWindow・NotificationWindow は WDA_EXCLUDEFROMCAPTURE で除外済みのため即時画面取得へ進む
                var bounds = _screenshotService.GetVirtualScreenBounds();

                if (key == CapturedKey.F)
                {
                    // Full screen capture directly
                    byte[]? imageBytes = await _screenshotService.CaptureRegionToBytesAsync(bounds);
                    if (imageBytes == null || imageBytes.Length == 0) return;
                    
                    var settings = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SettingsManager>(((App)Microsoft.UI.Xaml.Application.Current).Services);
                    if (settings.IsAutoCopyToClipboard)
                    {
                        // 自動クリップボードコピーON時はフォルダ選択画面を出さず直接コピー＋通知のみ行う
                        await CopyToClipboardAsync(imageBytes, true);
                        return;
                    }

                    SaveAndShowNotification(imageBytes);
                    return;
                }

                var frozenImage = await _screenshotService.CaptureFullScreenAsync();
                if (frozenImage == null) return;

                Rectangle? selectedRegion = null;
                byte[]? overlayCapturedBytes = null;
                OverlayWindow.CaptureAction overlayAction = OverlayWindow.CaptureAction.None;

                if (key == CapturedKey.C || key == CapturedKey.E || key == CapturedKey.P || key == CapturedKey.L)
                {
                    var overlay = GetOverlayWindow();
                    var tcs = new TaskCompletionSource<Rectangle?>();
                    
                    Action? handler = null;
                    handler = () =>
                    {
                        if (handler != null) overlay.CaptureCompleted -= handler;
                        if (overlay.IsSelected)
                        {
                            overlayCapturedBytes = overlay.CapturedBytes;
                            overlayAction = overlay.ActionTaken;
                            tcs.SetResult(overlay.SelectedRegion);
                        }
                        else
                        {
                            tcs.SetResult(null);
                        }
                    };
                    
                    overlay.CaptureCompleted += handler;
                    overlay.UpdateAndShow(frozenImage, bounds, key == CapturedKey.E, key == CapturedKey.P, key == CapturedKey.L);
                    
                    selectedRegion = await tcs.Task;
                }
                else if (key == CapturedKey.W)
                {
                    var windowOverlay = GetWindowSelectionOverlay();
                    var tcs = new TaskCompletionSource<Rectangle?>();
                    
                    Action? handler = null;
                    handler = () =>
                    {
                        if (handler != null) windowOverlay.CaptureCompleted -= handler;
                        if (windowOverlay.IsSelected)
                        {
                            overlayAction = windowOverlay.ActionTaken;
                            tcs.SetResult(windowOverlay.SelectedRegion);
                        }
                        else
                        {
                            tcs.SetResult(null);
                        }
                    };
                    
                    windowOverlay.CaptureCompleted += handler;
                    windowOverlay.UpdateAndShow(frozenImage, bounds);
                    
                    selectedRegion = await tcs.Task;
                }

                if (selectedRegion.HasValue)
                {
                    if (key == CapturedKey.L)
                    {
                        var liveRect = new Rectangle(
                            selectedRegion.Value.X + bounds.X,
                            selectedRegion.Value.Y + bounds.Y,
                            selectedRegion.Value.Width,
                            selectedRegion.Value.Height
                        );
                        var livePinWindow = new LivePinWindow(liveRect, _screenshotService);
                        livePinWindow.Activate();
                        return;
                    }

                    byte[]? finalBytes = overlayCapturedBytes;

                    if (finalBytes == null || finalBytes.Length == 0)
                    {
                        var absRect = new Rectangle(
                            selectedRegion.Value.X + bounds.X,
                            selectedRegion.Value.Y + bounds.Y,
                            selectedRegion.Value.Width,
                            selectedRegion.Value.Height
                        );
                        
                        await Task.Run(() => DwmFlush());
                        finalBytes = await _screenshotService.CaptureRegionToBytesAsync(absRect);
                    }
                    
                    if (key == CapturedKey.P)
                    {
                        if (finalBytes != null && finalBytes.Length > 0)
                        {
                            var pinWindow = new PinWindow(finalBytes);
                            pinWindow.Activate();
                        }
                        return;
                    }

                    if (finalBytes != null && finalBytes.Length > 0)
                    {
                        var settings = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SettingsManager>(((App)Microsoft.UI.Xaml.Application.Current).Services);
                        if (overlayAction == OverlayWindow.CaptureAction.Copy ||
                            (key == CapturedKey.W && settings.IsAutoCopyToClipboard))
                        {
                            await CopyToClipboardAsync(finalBytes, true);
                        }
                        else if (overlayAction == OverlayWindow.CaptureAction.Save)
                        {
                            SaveAndShowNotification(finalBytes);
                        }
                    }
                }
            }
            finally
            {
                rewindService?.Resume();
            }
        }

        private Task CopyToClipboardAsync(byte[] imageBytes, bool showNotification)
        {
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var clipboardService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ClipboardService>(app.Services);
            var dispatcher = app.MainWindow?.DispatcherQueue ?? _editorWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            return clipboardService.CopyImageAsync(imageBytes, showNotification, dispatcher);
        }

        private void SaveAndShowNotification(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0) return;

            var app = (App)Microsoft.UI.Xaml.Application.Current;
            var folderWin = GetFolderSelectionWindow();
            var dispatcher = folderWin.DispatcherQueue ?? app.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(async () => 
                {
                    try
                    {
                        await folderWin.ShowAsync(imageBytes);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to show folder selection: {ex.Message}");
                    }
                });
            }
        }
    }
}
