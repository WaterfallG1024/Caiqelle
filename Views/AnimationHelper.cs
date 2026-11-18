using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Caiqelle.Views
{
    /// <summary>
    /// UI 要素に対する Composition スプリング拡大縮小アニメーションの共通ヘルパー
    /// </summary>
    public static class AnimationHelper
    {
        public static void AnimateScale(object sender, float scale, float damping, int periodMs)
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

        public static void OnPointerEnteredScale(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Control c && !c.IsEnabled) return;
            AnimateScale(sender, 1.05f, 0.5f, 40);
        }

        public static void OnPointerExitedScale(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => AnimateScale(sender, 1.0f, 0.6f, 50);

        public static void OnPointerPressedScale(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Control c && !c.IsEnabled) return;
            AnimateScale(sender, 0.95f, 0.4f, 30);
        }

        public static void OnPointerReleasedScale(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Control c && !c.IsEnabled) return;
            AnimateScale(sender, 1.05f, 0.5f, 40);
        }
    }
}
