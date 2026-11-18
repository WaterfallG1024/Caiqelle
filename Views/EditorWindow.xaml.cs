using Caiqelle.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Caiqelle.Views
{
    public sealed partial class EditorWindow : Window
    {
        public EditorViewModel ViewModel { get; }

        private byte[]? _currentImageBytes;
        
        // Drawing State
        private bool _isDrawing = false;
        private Windows.Foundation.Point _startPoint;
        
        // Active Elements for preview
        private Polyline? _currentPolyline;
        private Microsoft.UI.Xaml.Shapes.Line? _currentLine;
        private Microsoft.UI.Xaml.Shapes.Path? _currentArrow;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _currentRect;
        
        // Undo/Redo Stacks
        private readonly System.Collections.Generic.Stack<Microsoft.UI.Xaml.UIElement> _undoStack = new();
        private readonly System.Collections.Generic.Stack<Microsoft.UI.Xaml.UIElement> _redoStack = new();
        
        // Step Numbering counter
        private int _stepNumber = 1;

        public EditorWindow()
        {
            var app = (App)Application.Current;
            ViewModel = app.Services.GetRequiredService<EditorViewModel>();
            ViewModel.RequestUndo = PerformUndo;
            ViewModel.RequestRedo = PerformRedo;
            ViewModel.RequestClear = PerformClear;
            ViewModel.RequestCopyAsync = PerformCopyAsync;
            ViewModel.RequestSaveAsync = PerformSaveAsync;

            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            this.Closed += EditorWindow_Closed;
        }

        private void EditorWindow_Closed(object sender, WindowEventArgs args)
        {
            args.Handled = true;
            this.Hide(); 
        }

        public async Task LoadImageAsync(byte[] imageBytes)
        {
            _currentImageBytes = imageBytes;
            
            using var ms = new MemoryStream(imageBytes);
            var randomAccessStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await randomAccessStream.WriteAsync(ms.ToArray().AsBuffer());
            randomAccessStream.Seek(0);
            
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccessStream);
            CapturedImage.Source = bitmap;

            // BitmapImage.PixelWidth は描画前だと 0 になる場合があるため、元画像データから実サイズを取得
            int imgWidth = bitmap.PixelWidth;
            int imgHeight = bitmap.PixelHeight;
            if (imgWidth <= 0 || imgHeight <= 0)
            {
                using var sizeMs = new MemoryStream(imageBytes);
                using var img = System.Drawing.Image.FromStream(sizeMs, false, false);
                imgWidth = img.Width;
                imgHeight = img.Height;
            }

            CapturedImage.Width = imgWidth;
            CapturedImage.Height = imgHeight;
            DrawCanvas.Width = imgWidth;
            DrawCanvas.Height = imgHeight;

            // Reset tools
            DrawCanvas.Children.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _stepNumber = 1;
            UpdateUndoRedoButtons();
            
            // Maximize before showing
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var presenter = appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter != null)
            {
                presenter.Maximize();
            }

            // Show window
            this.Show();
            this.Activate();
        }

        private void UpdateUndoRedoButtons()
        {
            ViewModel.CanUndo = _undoStack.Count > 0;
            ViewModel.CanRedo = _redoStack.Count > 0;
        }

        private void PerformUndo()
        {
            if (_undoStack.Count > 0)
            {
                var el = _undoStack.Pop();
                DrawCanvas.Children.Remove(el);
                _redoStack.Push(el);
                UpdateUndoRedoButtons();
            }
        }

        private void PerformRedo()
        {
            if (_redoStack.Count > 0)
            {
                var el = _redoStack.Pop();
                DrawCanvas.Children.Add(el);
                _undoStack.Push(el);
                UpdateUndoRedoButtons();
            }
        }

        private void DrawCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(DrawCanvas);
            if (!ptr.Properties.IsLeftButtonPressed) return;

            var rawStart = ptr.Position;
            double canvasW = DrawCanvas.ActualWidth > 0 ? DrawCanvas.ActualWidth : DrawCanvas.Width;
            double canvasH = DrawCanvas.ActualHeight > 0 ? DrawCanvas.ActualHeight : DrawCanvas.Height;
            _startPoint = new Windows.Foundation.Point(
                Math.Max(0, Math.Min(rawStart.X, canvasW)),
                Math.Max(0, Math.Min(rawStart.Y, canvasH))
            );

            _isDrawing = true;
            _redoStack.Clear();

            var brush = ViewModel.SelectedColorBrush;
            var currentTool = ViewModel.CurrentTool;

            if (currentTool == EditorTool.Pen)
            {
                _currentPolyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = 4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                _currentPolyline.Points.Add(_startPoint);
                DrawCanvas.Children.Add(_currentPolyline);
                _undoStack.Push(_currentPolyline);
            }
            else if (currentTool == EditorTool.Line)
            {
                _currentLine = new Microsoft.UI.Xaml.Shapes.Line { Stroke = brush, StrokeThickness = 4, X1 = _startPoint.X, Y1 = _startPoint.Y, X2 = _startPoint.X, Y2 = _startPoint.Y, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                DrawCanvas.Children.Add(_currentLine);
                _undoStack.Push(_currentLine);
            }
            else if (currentTool == EditorTool.Arrow)
            {
                _currentArrow = new Microsoft.UI.Xaml.Shapes.Path { Stroke = brush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                UpdateArrowPath(_startPoint, _startPoint);
                DrawCanvas.Children.Add(_currentArrow);
                _undoStack.Push(_currentArrow);
            }
            else if (currentTool == EditorTool.Rect)
            {
                _currentRect = new Microsoft.UI.Xaml.Shapes.Rectangle { StrokeThickness = 4 };
                if (ViewModel.IsFillEnabled)
                {
                    _currentRect.Fill = brush;
                    _currentRect.Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                else
                {
                    _currentRect.Stroke = brush;
                    _currentRect.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                
                Canvas.SetLeft(_currentRect, _startPoint.X);
                Canvas.SetTop(_currentRect, _startPoint.Y);
                _currentRect.Width = 0;
                _currentRect.Height = 0;
                DrawCanvas.Children.Add(_currentRect);
                _undoStack.Push(_currentRect);
            }
            else if (currentTool == EditorTool.Number)
            {
                DrawStepNumber(_startPoint);
                _isDrawing = false;
                UpdateUndoRedoButtons();
                return;
            }

            DrawCanvas.CapturePointer(e.Pointer);
            UpdateUndoRedoButtons();
            e.Handled = true;
        }

        private void DrawCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing) return;
            e.Handled = true;
            var ptr = e.GetCurrentPoint(DrawCanvas);
            var rawPos = ptr.Position;
            double canvasW = DrawCanvas.ActualWidth > 0 ? DrawCanvas.ActualWidth : DrawCanvas.Width;
            double canvasH = DrawCanvas.ActualHeight > 0 ? DrawCanvas.ActualHeight : DrawCanvas.Height;

            var pos = new Windows.Foundation.Point(
                Math.Max(0, Math.Min(rawPos.X, canvasW)),
                Math.Max(0, Math.Min(rawPos.Y, canvasH))
            );

            var currentTool = ViewModel.CurrentTool;
            if (currentTool == EditorTool.Pen && _currentPolyline != null)
            {
                _currentPolyline.Points.Add(pos);
            }
            else if (currentTool == EditorTool.Line && _currentLine != null)
            {
                _currentLine.X2 = pos.X;
                _currentLine.Y2 = pos.Y;
            }
            else if (currentTool == EditorTool.Arrow && _currentArrow != null)
            {
                UpdateArrowPath(_startPoint, pos);
            }
            else if (currentTool == EditorTool.Rect && _currentRect != null)
            {
                Canvas.SetLeft(_currentRect, Math.Min(_startPoint.X, pos.X));
                Canvas.SetTop(_currentRect, Math.Min(_startPoint.Y, pos.Y));
                _currentRect.Width = Math.Abs(pos.X - _startPoint.X);
                _currentRect.Height = Math.Abs(pos.Y - _startPoint.Y);
            }
        }

        private void DrawCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDrawing) return;
            _isDrawing = false;
            DrawCanvas.ReleasePointerCapture(e.Pointer);

            _currentPolyline = null;
            _currentLine = null;
            _currentArrow = null;
            _currentRect = null;
            e.Handled = true;
        }

        private void UpdateArrowPath(Windows.Foundation.Point p1, Windows.Foundation.Point p2)
        {
            if (_currentArrow == null) return;
            var geom = new Microsoft.UI.Xaml.Media.PathGeometry();
            var fig = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = p1, IsClosed = false };
            fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = p2 });

            double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            double arrowLength = 15;
            double arrowAngle = Math.PI / 6; // 30 degrees

            var p3 = new Windows.Foundation.Point(p2.X - arrowLength * Math.Cos(angle - arrowAngle), p2.Y - arrowLength * Math.Sin(angle - arrowAngle));
            var p4 = new Windows.Foundation.Point(p2.X - arrowLength * Math.Cos(angle + arrowAngle), p2.Y - arrowLength * Math.Sin(angle + arrowAngle));

            fig.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = p3 });
            
            var fig2 = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = p2, IsClosed = false };
            fig2.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = p4 });

            geom.Figures.Add(fig);
            geom.Figures.Add(fig2);
            _currentArrow.Data = geom;
        }

        private void DrawStepNumber(Windows.Foundation.Point position)
        {
            var grid = new Grid
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = ViewModel.SelectedColorBrush
            };

            var text = new TextBlock
            {
                Text = _stepNumber.ToString(),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 16
            };
            grid.Children.Add(text);
            
            Canvas.SetLeft(grid, position.X - 16);
            Canvas.SetTop(grid, position.Y - 16);
            DrawCanvas.Children.Add(grid);
            
            grid.Tag = "StepNumber";
            
            _undoStack.Push(grid);
            _stepNumber++;
        }

        private void PerformClear()
        {
            DrawCanvas.Children.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _stepNumber = 1;
            UpdateUndoRedoButtons();
        }

        private async Task PerformSaveAsync()
        {
            var settings = ((App)Application.Current).Services.GetRequiredService<Caiqelle.Services.SettingsManager>();
            using var bmp = GetCompositedBitmap();
            byte[] finalBytes = EncodeBitmap(bmp, settings.ImageFormat, settings.JpegQuality);
            
            var folderWindow = new FolderSelectionWindow((App)Application.Current);
            await folderWindow.ShowAsync(finalBytes);
        }

        private static System.Drawing.Color ToDrawingColor(Brush? brush, System.Drawing.Color fallback)
        {
            if (brush is SolidColorBrush sb)
            {
                return System.Drawing.Color.FromArgb(sb.Color.A, sb.Color.R, sb.Color.G, sb.Color.B);
            }
            return fallback;
        }

        private static System.Drawing.Pen CreateRoundedPen(Brush? brush, double thickness)
        {
            var color = ToDrawingColor(brush, System.Drawing.Color.Red);
            var pen = new System.Drawing.Pen(color, (float)(thickness > 0 ? thickness : 4));
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            return pen;
        }

        private System.Drawing.Bitmap GetCompositedBitmap()
        {
            if (_currentImageBytes == null) return new System.Drawing.Bitmap(1, 1);

            using var ms = new MemoryStream(_currentImageBytes);
            using var src = new System.Drawing.Bitmap(ms);
            // Stream 依存やインデックス形式を回避するため 32bppArgb の独立した Bitmap に複製
            var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.DrawImage(src, 0, 0, src.Width, src.Height);

            if (DrawCanvas.Children.Count == 0) return bmp;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var font = new System.Drawing.Font("Arial", 18, System.Drawing.FontStyle.Bold);
            using var brushText = new System.Drawing.SolidBrush(System.Drawing.Color.White);

            foreach (var child in DrawCanvas.Children)
            {
                if (child is Polyline polyline && polyline.Points.Count > 1)
                {
                    using var pen = CreateRoundedPen(polyline.Stroke, polyline.StrokeThickness);
                    var pts = new System.Drawing.PointF[polyline.Points.Count];
                    for (int i = 0; i < polyline.Points.Count; i++)
                    {
                        pts[i] = new System.Drawing.PointF((float)polyline.Points[i].X, (float)polyline.Points[i].Y);
                    }
                    g.DrawLines(pen, pts);
                }
                else if (child is Microsoft.UI.Xaml.Shapes.Line line)
                {
                    using var pen = CreateRoundedPen(line.Stroke, line.StrokeThickness);
                    g.DrawLine(pen, (float)line.X1, (float)line.Y1, (float)line.X2, (float)line.Y2);
                }
                else if (child is Microsoft.UI.Xaml.Shapes.Rectangle rect)
                {
                    if (rect.Fill is SolidColorBrush sb && sb.Color.A > 0)
                    {
                        using var b = new System.Drawing.SolidBrush(ToDrawingColor(sb, System.Drawing.Color.Red));
                        g.FillRectangle(b, (float)Canvas.GetLeft(rect), (float)Canvas.GetTop(rect), (float)rect.Width, (float)rect.Height);
                    }
                    if (rect.Stroke is SolidColorBrush sbStroke && sbStroke.Color.A > 0 && rect.StrokeThickness > 0)
                    {
                        using var p = new System.Drawing.Pen(ToDrawingColor(sbStroke, System.Drawing.Color.Red), (float)rect.StrokeThickness);
                        g.DrawRectangle(p, (float)Canvas.GetLeft(rect), (float)Canvas.GetTop(rect), (float)rect.Width, (float)rect.Height);
                    }
                }
                else if (child is Microsoft.UI.Xaml.Shapes.Path path && path.Data is Microsoft.UI.Xaml.Media.PathGeometry pg)
                {
                    using var pen = CreateRoundedPen(path.Stroke, path.StrokeThickness);
                    foreach (var fig in pg.Figures)
                    {
                        var start = fig.StartPoint;
                        foreach (var seg in fig.Segments)
                        {
                            if (seg is Microsoft.UI.Xaml.Media.LineSegment ls)
                            {
                                g.DrawLine(pen, (float)start.X, (float)start.Y, (float)ls.Point.X, (float)ls.Point.Y);
                                start = ls.Point;
                            }
                        }
                    }
                }
                else if (child is Grid grid && grid.Tag?.ToString() == "StepNumber")
                {
                    float x = (float)Canvas.GetLeft(grid);
                    float y = (float)Canvas.GetTop(grid);
                    using var brushBg = new System.Drawing.SolidBrush(ToDrawingColor(grid.Background, System.Drawing.Color.Red));
                    g.FillEllipse(brushBg, x, y, 32, 32);

                    if (grid.Children.Count > 0 && grid.Children[0] is TextBlock tb)
                    {
                        using var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
                        g.DrawString(tb.Text, font, brushText, new System.Drawing.RectangleF(x, y, 32, 32), sf);
                    }
                }
            }

            return bmp;
        }

        private byte[] EncodeBitmap(System.Drawing.Bitmap bmp, string format, int quality)
        {
            using var ms = new MemoryStream();
            if (format.ToUpper() == "JPEG" || format.ToUpper() == "JPG")
            {
                var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(ms, encoder, encParams);
            }
            else if (format.ToUpper() == "WEBP")
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            }
            else
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            }
            return ms.ToArray();
        }

        private void EditorWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrlDown)
            {
                if (e.Key == Windows.System.VirtualKey.S)
                {
                    ViewModel.SaveCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Windows.System.VirtualKey.C)
                {
                    ViewModel.CopyCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Windows.System.VirtualKey.Z)
                {
                    if (ViewModel.UndoCommand.CanExecute(null)) ViewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Windows.System.VirtualKey.Y)
                {
                    if (ViewModel.RedoCommand.CanExecute(null)) ViewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
        }

        private async Task PerformCopyAsync()
        {
            var settings = ((App)Application.Current).Services.GetRequiredService<Caiqelle.Services.SettingsManager>();
            using var bmp = GetCompositedBitmap();
            byte[] finalBytes = EncodeBitmap(bmp, settings.ImageFormat, settings.JpegQuality);
            
            var dataPackage = new DataPackage();
            dataPackage.RequestedOperation = DataPackageOperation.Copy;
            
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(finalBytes.AsBuffer());
            stream.Seek(0);
            
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(dataPackage);
        }
    }
    
    public static class WindowExtensions
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;
        
        public static void Hide(this Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ShowWindow(hwnd, SW_HIDE);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_SHOW = 5;

        public static void Show(this Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
    }
}
