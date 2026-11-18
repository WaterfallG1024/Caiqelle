using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace Caiqelle.Views
{
    /// <summary>
    /// 描画キャンバス共通の矢印ジオメトリ更新・個別削除ヒットテストヘルパー
    /// </summary>
    public static class AnnotationHelper
    {
        /// <summary>
        /// 始点と終点から矢印の PathGeometry を生成して設定する
        /// </summary>
        public static void UpdateArrowPath(Microsoft.UI.Xaml.Shapes.Path arrowPath, Windows.Foundation.Point p1, Windows.Foundation.Point p2)
        {
            const double headLen = 15;
            const double headAngle = Math.PI / 6;
            double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);

            var p3 = new Windows.Foundation.Point(
                p2.X - headLen * Math.Cos(angle - headAngle),
                p2.Y - headLen * Math.Sin(angle - headAngle));
            var p4 = new Windows.Foundation.Point(
                p2.X - headLen * Math.Cos(angle + headAngle),
                p2.Y - headLen * Math.Sin(angle + headAngle));

            var geom = new PathGeometry();
            var fig = new PathFigure { StartPoint = p1, IsClosed = false };
            fig.Segments.Add(new LineSegment { Point = p2 });
            fig.Segments.Add(new LineSegment { Point = p3 });

            var fig2 = new PathFigure { StartPoint = p2, IsClosed = false };
            fig2.Segments.Add(new LineSegment { Point = p4 });

            geom.Figures.Add(fig);
            geom.Figures.Add(fig2);
            arrowPath.Data = geom;
        }

        /// <summary>
        /// 指定したウィンドウ座標周辺の描画要素を1つ検出してキャンバスから削除する
        /// </summary>
        public static bool TryEraseAtPoint(Canvas canvas, Windows.Foundation.Point hostPos)
        {
            var hitRect = new Windows.Foundation.Rect(hostPos.X - 6, hostPos.Y - 6, 12, 12);
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(hitRect, canvas);
            foreach (var hit in hits)
            {
                DependencyObject? current = hit;
                while (current != null && current != canvas)
                {
                    if (current is UIElement el && canvas.Children.Contains(el))
                    {
                        canvas.Children.Remove(el);
                        return true;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            return false;
        }
    }
}
