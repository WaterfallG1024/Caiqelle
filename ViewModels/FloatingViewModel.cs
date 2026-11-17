using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using Caiqelle.Services;

namespace Caiqelle.ViewModels
{
    /// <summary>
    /// FloatingWindow のキャプチャ起動コマンドを管理する ViewModel
    /// </summary>
    public partial class FloatingViewModel : ObservableObject
    {
        /// <summary>
        /// キャプチャ開始前にフローティングウィンドウを非表示にするためのコールバック
        /// </summary>
        public Action? RequestHideWindow { get; set; }

        /// <summary>
        /// CaptureManager 取得用デリゲート
        /// </summary>
        public Func<CaptureManager?>? GetCaptureManager { get; set; }

        [RelayCommand]
        private Task CaptureRegionAsync() => ExecuteCaptureAsync(CapturedKey.C);

        [RelayCommand]
        private Task CaptureWindowAsync() => ExecuteCaptureAsync(CapturedKey.W);

        [RelayCommand]
        private Task CaptureFullscreenAsync() => ExecuteCaptureAsync(CapturedKey.F);

        [RelayCommand]
        private Task CapturePinAsync() => ExecuteCaptureAsync(CapturedKey.P);

        [RelayCommand]
        private Task CaptureLivePinAsync() => ExecuteCaptureAsync(CapturedKey.L);

        [RelayCommand]
        private Task CaptureOcrAsync() => ExecuteCaptureAsync(CapturedKey.E);

        [RelayCommand]
        private void OpenRewind()
        {
            RequestHideWindow?.Invoke();
            ((App)Microsoft.UI.Xaml.Application.Current).ShowMainWindowAtTab(MainTab.Rewind);
        }

        [RelayCommand]
        private void Hide() => RequestHideWindow?.Invoke();

        private async Task ExecuteCaptureAsync(CapturedKey key)
        {
            RequestHideWindow?.Invoke();
            var captureManager = GetCaptureManager?.Invoke();
            if (captureManager != null)
            {
                await captureManager.StartCaptureAsync(key);
            }
        }
    }
}
