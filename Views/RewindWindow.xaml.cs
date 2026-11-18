using Caiqelle.Services;
using Caiqelle.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Caiqelle.Views
{
    public sealed partial class RewindWindow : Window
    {
        private readonly RewindService _rewindService;
        public RewindViewModel ViewModel { get; }

        public RewindWindow(RewindService rewindService, EditorWindow editorWindow)
        {
            _rewindService = rewindService;
            ViewModel = new RewindViewModel(rewindService, editorWindow);
            ViewModel.RequestClose = () => this.Close();

            this.InitializeComponent();
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Microsoft.UI.Colors.Transparent);
            ExtendsContentIntoTitleBar = true;

            // プレビュー表示中はリワインド録画を一時停止（映り込み防止）
            _rewindService.Pause();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindow.SetIcon("Assets\\favicon.ico");

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            // OS レベルで本ウィンドウを画面キャプチャから除外
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);

            bool isClosing = false;

            appWindow.Changed += (s, args) =>
            {
                if (isClosing) return;

                if ((args.DidPresenterChange || args.DidSizeChange || args.DidVisibilityChange)
                    && s.Presenter is OverlappedPresenter op)
                {
                    if (op.State == OverlappedPresenterState.Minimized)
                    {
                        _rewindService.Resume();
                    }
                    else
                    {
                        _rewindService.Pause();
                    }
                }
            };

            this.VisibilityChanged += (s, args) =>
            {
                if (isClosing) return;

                if (!args.Visible) _rewindService.Resume();
                else _rewindService.Pause();
            };

            this.Closed += (s, e) =>
            {
                isClosing = true;
                _rewindService.Resume();
                ViewModel.ClearFrames();
            };

            ViewModel.LoadFrames();
            BringToForeground(hWnd);
            RootGrid.Loaded += (s, e) => RootGrid.Focus(FocusState.Programmatic);
            RootGrid.Focus(FocusState.Programmatic);
        }

        private void Window_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(this.Content).Properties;
            ViewModel.Navigate(properties.MouseWheelDelta > 0 ? -1 : 1);
        }

        private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrl && e.Key == Windows.System.VirtualKey.C)
            {
                ViewModel.CopyToClipboardCommand.Execute(null);
                e.Handled = true;
            }
            else if (isCtrl && e.Key == Windows.System.VirtualKey.S)
            {
                ViewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Left) ViewModel.Navigate(1);
            else if (e.Key == Windows.System.VirtualKey.Right) ViewModel.Navigate(-1);
            else if (e.Key == Windows.System.VirtualKey.Enter) ViewModel.OpenInEditorCommand.Execute(null);
            else if (e.Key == Windows.System.VirtualKey.Escape) ViewModel.CloseCommand.Execute(null);
        }

        private void PreviewImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenInEditorCommand.Execute(null);
        }

        private static void AnimateButtonScale(object sender, float scale, float damping, int periodMs)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;

                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(scale, scale, 1.0f);
                animation.DampingRatio = damping;
                animation.Period = TimeSpan.FromMilliseconds(periodMs);

                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }

        private void ActionBtn_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Control c && !c.IsEnabled) return;
            AnimateButtonScale(sender, 1.05f, 0.5f, 40);
        }

        private void ActionBtn_PointerExited(object sender, PointerRoutedEventArgs e)
            => AnimateButtonScale(sender, 1.0f, 0.6f, 50);

        private void ActionBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Control c && !c.IsEnabled) return;
            AnimateButtonScale(sender, 0.95f, 0.4f, 30);
        }

        private void ActionBtn_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Control c && !c.IsEnabled) return;
            AnimateButtonScale(sender, 1.05f, 0.5f, 40);
        }

        private static void BringToForeground(IntPtr hWnd)
        {
            ShowWindow(hWnd, SW_SHOWMAXIMIZED);
            SetForegroundWindow(hWnd);
        }

        private const int SW_SHOWMAXIMIZED = 3;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
