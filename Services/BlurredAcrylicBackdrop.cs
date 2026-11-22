using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Caiqelle.Services
{
    /// <summary>
    /// 後ろのデスクトップやウィンドウをリアルタイムでぼかす透過アクリルブラー背景
    /// </summary>
    public sealed class BlurredAcrylicBackdrop : SystemBackdrop
    {
        private DesktopAcrylicController? _controller;
        private SystemBackdropConfiguration? _configuration;

        public Color TintColor { get; set; } = Color.FromArgb(255, 16, 16, 18);
        public float TintOpacity { get; set; } = 0.08f;
        public float LuminosityOpacity { get; set; } = 0.25f;
        public Color FallbackColor { get; set; } = Color.FromArgb(220, 18, 18, 20);

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);

            if (!DesktopAcrylicController.IsSupported()) return;

            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark
            };

            _controller = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
                TintColor = TintColor,
                TintOpacity = TintOpacity,
                LuminosityOpacity = LuminosityOpacity,
                FallbackColor = FallbackColor
            };

            _controller.AddSystemBackdropTarget(connectedTarget);
            _controller.SetSystemBackdropConfiguration(_configuration);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);

            if (_controller != null)
            {
                _controller.RemoveSystemBackdropTarget(disconnectedTarget);
                _controller.Dispose();
                _controller = null;
            }
            _configuration = null;
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            // 自前の IsInputActive = true 構成を維持するため上書きしない
        }
    }
}
