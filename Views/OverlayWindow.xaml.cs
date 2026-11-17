using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.IO;

namespace Caiqelle.Views
{
    public sealed partial class OverlayWindow : Window
    {
        private enum OverlayState { Initial, Selecting, Adjusting }
        private OverlayState _state = OverlayState.Initial;
        
        private Windows.Foundation.Point _startPoint;
        private bool _isDragging = false;
        private bool _isResizing = false;
        private FrameworkElement? _currentHandle = null;
        
        // Drawing
        public enum EditorTool { Pen, Line, Arrow, Rect, Number }
        private EditorTool _currentTool = EditorTool.Pen;
        private bool _isDrawing = false;
        private SolidColorBrush _currentBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
        private bool _isFillEnabled = false;
        private Polyline? _currentPolyline;
        private Microsoft.UI.Xaml.Shapes.Line? _currentLine;
        private Microsoft.UI.Xaml.Shapes.Path? _currentArrow;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _currentRect;
        private Stack<UIElement> _undoStack = new Stack<UIElement>();
        private Stack<UIElement> _redoStack = new Stack<UIElement>();
        private int _stepNumber = 1;
        
        public System.Drawing.Rectangle SelectedRegion { get; private set; }
        public bool IsSelected { get; private set; }
        public Microsoft.UI.Xaml.Media.ImageSource? FrozenImage { get; private set; }
        public new Windows.Foundation.Rect Bounds { get; private set; }
        public byte[]? CapturedBytes { get; private set; }
        
        public enum CaptureAction { None, Copy, Save }
        public CaptureAction ActionTaken { get; private set; } = CaptureAction.None;
        private bool _isOcrMode = false;

        public event Action? CaptureCompleted;

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private System.Drawing.Rectangle _lastBounds = System.Drawing.Rectangle.Empty;

        public OverlayWindow()
        {
            this.InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            // ウィンドウの白い境界線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2; // 0xFFFFFFFE
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);

            // Attach hit testing to handles
            HandleTL.PointerPressed += Handle_PointerPressed;
            HandleTR.PointerPressed += Handle_PointerPressed;
            HandleBL.PointerPressed += Handle_PointerPressed;
            HandleBR.PointerPressed += Handle_PointerPressed;
            HandleT.PointerPressed += Handle_PointerPressed;
            HandleB.PointerPressed += Handle_PointerPressed;
            HandleL.PointerPressed += Handle_PointerPressed;
            HandleR.PointerPressed += Handle_PointerPressed;

            FloatingToolbar.SizeChanged += (s, e) =>
            {
                if (FloatingToolbar.Visibility == Visibility.Visible && _state == OverlayState.Adjusting)
                {
                    PositionToolbar();
                }
            };

            this.Activated += OverlayWindow_Activated;
        }

        public void UpdateAndShow(Microsoft.UI.Xaml.Media.ImageSource frozenImage, System.Drawing.Rectangle bounds, bool isOcrMode = false, bool isPinMode = false)
        {
            _isOcrMode = isOcrMode;
            
            if (isPinMode)
            {
                ToolTipService.SetToolTip(SaveButton, "ピン留め(またはCtrl + S)");
                if (SaveButton.Content is FontIcon icon) icon.Glyph = "\uE718"; // Pin icon
            }
            else
            {
                ToolTipService.SetToolTip(SaveButton, "保存(またはCtrl + S)");
                if (SaveButton.Content is FontIcon icon) icon.Glyph = "\uE792"; // Save icon
            }
            FrozenImage = frozenImage;
            BackgroundImage.Source = frozenImage;

            if (_lastBounds != bounds)
            {
                _lastBounds = bounds;
                Bounds = new Windows.Foundation.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                OuterDimmerRect.Rect = Bounds;
            }

            CutoutRect.Rect = new Windows.Foundation.Rect(0, 0, 0, 0);

            _state = OverlayState.Initial;
            SelectionBox.Visibility = Visibility.Collapsed;
            FloatingToolbar.Visibility = Visibility.Collapsed;
            OverlayDimmer.Visibility = Visibility.Visible;
            SetHandlesVisibility(Visibility.Collapsed);
            AnnotationCanvas.Visibility = Visibility.Collapsed;
            if (AnnotationCanvas.Children.Count > 0) AnnotationCanvas.Children.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _stepNumber = 1;
            UpdateUndoRedoButtons();

            _appWindow.Show();
        }

        private void HideAndComplete()
        {
            _appWindow.Hide();
            CaptureCompleted?.Invoke();
        }

        private void OverlayWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            DrawCanvas.Focus(FocusState.Programmatic);
        }

        private void Window_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointerPoint = e.GetCurrentPoint(DrawCanvas);
            if (!pointerPoint.Properties.IsLeftButtonPressed) return;

            if (_state == OverlayState.Initial)
            {
                _state = OverlayState.Selecting;
                _isDragging = true;
                _startPoint = pointerPoint.Position;
                SelectionBox.Visibility = Visibility.Visible;
                UpdateSelectionBox(_startPoint.X, _startPoint.Y, 0, 0);
                DrawCanvas.CapturePointer(e.Pointer);
            }
            else if (_state == OverlayState.Adjusting)
            {
                if (_isResizing) return; // handled by Handle_PointerPressed
                
                // If inside selection box, start drawing
                var p = pointerPoint.Position;
                var rect = CutoutRect.Rect;
                if (p.X >= rect.X && p.X <= rect.X + rect.Width && p.Y >= rect.Y && p.Y <= rect.Y + rect.Height)
                {
                    StartDrawing(p);
                    DrawCanvas.CapturePointer(e.Pointer);
                }
                else
                {
                    // Clicked outside, start new selection
                    _state = OverlayState.Selecting;
                    _isDragging = true;
                    _startPoint = pointerPoint.Position;
                    FloatingToolbar.Visibility = Visibility.Collapsed;
                    SetHandlesVisibility(Visibility.Collapsed);
                    AnnotationCanvas.Visibility = Visibility.Collapsed;
                    UpdateSelectionBox(_startPoint.X, _startPoint.Y, 0, 0);
                    DrawCanvas.CapturePointer(e.Pointer);
                }
            }
        }

        private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_state == OverlayState.Adjusting)
            {
                _isResizing = true;
                _currentHandle = sender as FrameworkElement;
                _startPoint = e.GetCurrentPoint(DrawCanvas).Position;
                DrawCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void Window_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var currentPoint = e.GetCurrentPoint(DrawCanvas).Position;

            if (_state == OverlayState.Selecting && _isDragging)
            {
                double x = Math.Min(currentPoint.X, _startPoint.X);
                double y = Math.Min(currentPoint.Y, _startPoint.Y);
                double width = Math.Abs(currentPoint.X - _startPoint.X);
                double height = Math.Abs(currentPoint.Y - _startPoint.Y);
                UpdateSelectionBox(x, y, width, height);
            }
            else if (_state == OverlayState.Adjusting)
            {
                if (_isResizing && _currentHandle != null)
                {
                    double dx = currentPoint.X - _startPoint.X;
                    double dy = currentPoint.Y - _startPoint.Y;
                    _startPoint = currentPoint;
                    
                    var r = CutoutRect.Rect;
                    double newX = r.X;
                    double newY = r.Y;
                    double newW = r.Width;
                    double newH = r.Height;

                    if (_currentHandle == HandleTL || _currentHandle == HandleL || _currentHandle == HandleBL)
                    {
                        newX += dx; newW -= dx;
                    }
                    if (_currentHandle == HandleTL || _currentHandle == HandleT || _currentHandle == HandleTR)
                    {
                        newY += dy; newH -= dy;
                    }
                    if (_currentHandle == HandleTR || _currentHandle == HandleR || _currentHandle == HandleBR)
                    {
                        newW += dx;
                    }
                    if (_currentHandle == HandleBL || _currentHandle == HandleB || _currentHandle == HandleBR)
                    {
                        newH += dy;
                    }

                    if (newW > 10 && newH > 10)
                    {
                        UpdateSelectionBox(newX, newY, newW, newH);
                        PositionToolbar();
                    }
                }
                else if (_isDrawing)
                {
                    UpdateDrawing(currentPoint);
                }
            }
        }

        private void Window_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            DrawCanvas.ReleasePointerCapture(e.Pointer);

            if (_state == OverlayState.Selecting)
            {
                _isDragging = false;
                var rect = CutoutRect.Rect;
                if (rect.Width > 10 && rect.Height > 10)
                {
                    if (_isOcrMode)
                    {
                        // Direct OCR processing
                        StartOcrScanAnimation();
                    }
                    else
                    {
                        _state = OverlayState.Adjusting;
                        SetHandlesVisibility(Visibility.Visible);
                        
                        AnnotationCanvas.Visibility = Visibility.Visible;
                        FloatingToolbar.Visibility = Visibility.Visible;
                        PositionToolbar();
                    }
                }
                else
                {
                    _state = OverlayState.Initial;
                    SelectionBox.Visibility = Visibility.Collapsed;
                    CutoutRect.Rect = new Windows.Foundation.Rect(0, 0, 0, 0);
                }
            }
            else if (_state == OverlayState.Adjusting)
            {
                if (_isResizing)
                {
                    _isResizing = false;
                    _currentHandle = null;
                }
                else if (_isDrawing)
                {
                    _isDrawing = false;
                }
            }
        }

        private void UpdateSelectionBox(double x, double y, double w, double h)
        {
            Canvas.SetLeft(SelectionBox, x);
            Canvas.SetTop(SelectionBox, y);
            SelectionBox.Width = w;
            SelectionBox.Height = h;
            CutoutRect.Rect = new Windows.Foundation.Rect(x, y, w, h);
            
            AnnotationCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = CutoutRect.Rect };

            UpdateHandles(x, y, w, h);
        }

        private void UpdateHandles(double x, double y, double w, double h)
        {
            Canvas.SetLeft(HandleTL, x - 2); Canvas.SetTop(HandleTL, y - 2);
            Canvas.SetLeft(HandleTR, x + w - 18); Canvas.SetTop(HandleTR, y - 2);
            Canvas.SetLeft(HandleBL, x - 2); Canvas.SetTop(HandleBL, y + h - 18);
            Canvas.SetLeft(HandleBR, x + w - 18); Canvas.SetTop(HandleBR, y + h - 18);
            
            Canvas.SetLeft(HandleT, x + w / 2 - 12); Canvas.SetTop(HandleT, y - 3);
            Canvas.SetLeft(HandleB, x + w / 2 - 12); Canvas.SetTop(HandleB, y + h - 3);
            Canvas.SetLeft(HandleL, x - 3); Canvas.SetTop(HandleL, y + h / 2 - 12);
            Canvas.SetLeft(HandleR, x + w - 3); Canvas.SetTop(HandleR, y + h / 2 - 12);
        }

        private void SetHandlesVisibility(Visibility v)
        {
            HandleTL.Visibility = v; HandleT.Visibility = v; HandleTR.Visibility = v;
            HandleL.Visibility = v; HandleR.Visibility = v;
            HandleBL.Visibility = v; HandleB.Visibility = v; HandleBR.Visibility = v;
        }

        private void PositionToolbar()
        {
            // ツールバーのサイズを計測
            FloatingToolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double tbW = FloatingToolbar.ActualWidth > 0 ? FloatingToolbar.ActualWidth : FloatingToolbar.DesiredSize.Width;
            double tbH = FloatingToolbar.ActualHeight > 0 ? FloatingToolbar.ActualHeight : FloatingToolbar.DesiredSize.Height;
            if (tbW <= 0) tbW = 540;
            if (tbH <= 0) tbH = 46;

            double screenW = Bounds.Width;
            double screenH = Bounds.Height;
            double selX = Canvas.GetLeft(SelectionBox);
            double selY = Canvas.GetTop(SelectionBox);
            double selW = SelectionBox.Width;
            double selH = SelectionBox.Height;

            // 基本配置：キャプチャ枠の右下に配置（右揃え）
            double x = selX + selW - tbW;
            double y = selY + selH + 12;

            // 画面右端・左端のクランプ
            if (x + tbW > screenW - 12)
            {
                x = screenW - tbW - 12;
            }
            if (x < 12)
            {
                x = 12;
            }

            // 画面下端のクランプ
            if (y + tbH > screenH - 12)
            {
                // 下部に入り切らない場合、キャプチャ枠の上に配置
                y = selY - tbH - 12;
                
                // 上部にも入り切らない場合（画面全体近くをキャプチャしている場合）、キャプチャ枠内の下部に配置
                if (y < 12)
                {
                    y = Math.Max(12, Math.Min(screenH - tbH - 12, selY + selH - tbH - 12));
                }
            }

            FloatingToolbar.Margin = new Thickness(x, y, 0, 0);
        }

        // --- Toolbar Events ---
        private void ToolBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            {
                PenBtn.IsChecked = false; LineBtn.IsChecked = false; ArrowBtn.IsChecked = false; RectBtn.IsChecked = false; NumberBtn.IsChecked = false;
                tb.IsChecked = true;
                if (tb == PenBtn) _currentTool = EditorTool.Pen;
                else if (tb == LineBtn) _currentTool = EditorTool.Line;
                else if (tb == ArrowBtn) _currentTool = EditorTool.Arrow;
                else if (tb == RectBtn) _currentTool = EditorTool.Rect;
                else if (tb == NumberBtn) _currentTool = EditorTool.Number;
            }
        }
        
        private void FillBtn_Click(object sender, RoutedEventArgs e) { _isFillEnabled = FillBtn.IsChecked == true; }
        
        private void EditorColorPicker_ColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            _currentBrush = new SolidColorBrush(args.NewColor);
            if (ColorPreviewBorder != null) ColorPreviewBorder.Background = _currentBrush;
        }

        private void UpdateUndoRedoButtons()
        {
            UndoBtn.IsEnabled = _undoStack.Count > 0;
            RedoBtn.IsEnabled = _redoStack.Count > 0;
        }

        private void UndoBtn_Click(object? sender, RoutedEventArgs? e)
        {
            if (_undoStack.Count > 0)
            {
                var el = _undoStack.Pop();
                AnnotationCanvas.Children.Remove(el);
                _redoStack.Push(el);
                UpdateUndoRedoButtons();
            }
        }

        private void RedoBtn_Click(object? sender, RoutedEventArgs? e)
        {
            if (_redoStack.Count > 0)
            {
                var el = _redoStack.Pop();
                AnnotationCanvas.Children.Add(el);
                _undoStack.Push(el);
                UpdateUndoRedoButtons();
            }
        }

        private void ClearButton_Click(object? sender, RoutedEventArgs? e)
        {
            AnnotationCanvas.Children.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _stepNumber = 1;
            UpdateUndoRedoButtons();
        }

        private async void StartOcrScanAnimation()
        {
            var rect = CutoutRect.Rect;
            OcrScanLine.Width = rect.Width;
            Canvas.SetLeft(OcrScanLine, rect.X);
            Canvas.SetTop(OcrScanLine, rect.Y);
            OcrScanLine.Visibility = Visibility.Visible;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(OcrScanLine);
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.0f, 0.0f);
            animation.InsertKeyFrame(0.5f, (float)rect.Height);
            animation.InsertKeyFrame(1.0f, 0.0f);
            animation.Duration = TimeSpan.FromSeconds(1.5);
            animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Count;
            animation.IterationCount = 100; // loop while processing

            visual.StartAnimation("Offset.Y", animation);

            await Task.Delay(200);

            ActionTaken = CaptureAction.Copy;
            SelectedRegion = new System.Drawing.Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
            CapturedBytes = await GetCompositedImageBytesAsync();

            try
            {
                var ocrService = new Caiqelle.Services.OcrService();
                string text = await ocrService.ExtractTextAsync(CapturedBytes);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                }
            }
            catch { }
            
            visual.StopAnimation("Offset.Y");
            OcrScanLine.Visibility = Visibility.Collapsed;

            ActionTaken = CaptureAction.None; 
            
            HideAndComplete();
        }

        private void CopyButton_Click(object? sender, RoutedEventArgs? e) { ActionTaken = CaptureAction.Copy; CompleteCapture(); }
        private void SaveButton_Click(object? sender, RoutedEventArgs? e) { ActionTaken = CaptureAction.Save; CompleteCapture(); }
        
        public void Cancel()
        {
            ActionTaken = CaptureAction.None;
            IsSelected = false;
            HideAndComplete();
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs? e)
        {
            Cancel();
        }

        private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (e.Key == Windows.System.VirtualKey.Escape) 
            {
                CancelButton_Click(null, null);
            }
            else if (e.Key == Windows.System.VirtualKey.Enter && _state == OverlayState.Adjusting) 
            {
                ActionTaken = CaptureAction.Save;
                CompleteCapture();
                e.Handled = true;
            }
            else if (isCtrlDown)
            {
                if (e.Key == Windows.System.VirtualKey.C)
                {
                    if (_state == OverlayState.Adjusting)
                    {
                        ActionTaken = CaptureAction.Copy;
                        CompleteCapture();
                        e.Handled = true;
                    }
                }
                else if (e.Key == Windows.System.VirtualKey.S)
                {
                    if (_state == OverlayState.Adjusting)
                    {
                        ActionTaken = CaptureAction.Save;
                        CompleteCapture();
                        e.Handled = true;
                    }
                }
                else if (e.Key == Windows.System.VirtualKey.Z)
                {
                    if (UndoBtn.IsEnabled) UndoBtn_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Y)
                {
                    if (RedoBtn.IsEnabled) RedoBtn_Click(null, null);
                    e.Handled = true;
                }
            }
        }

        private async void CompleteCapture()
        {
            var r = CutoutRect.Rect;
            SelectedRegion = new System.Drawing.Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
            IsSelected = true;

            // Hide UI (but keep dimmer to prevent flash effect)
            SelectionBox.Visibility = Visibility.Collapsed;
            FloatingToolbar.Visibility = Visibility.Collapsed;
            SetHandlesVisibility(Visibility.Collapsed);

            await Task.Delay(50);

            CapturedBytes = await GetCompositedImageBytesAsync();

            HideAndComplete();
        }

        private async Task<byte[]> GetCompositedImageBytesAsync()
        {
            var r = CutoutRect.Rect;
            int cx = (int)Math.Round(r.X);
            int cy = (int)Math.Round(r.Y);
            int cw = (int)Math.Round(r.Width);
            int ch = (int)Math.Round(r.Height);

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            var buffer = await rtb.GetPixelsAsync();
            byte[] pixels = buffer.ToArray();

            int width = rtb.PixelWidth;
            int height = rtb.PixelHeight;

            cx = Math.Max(0, cx);
            cy = Math.Max(0, cy);
            cw = Math.Min(cw, width - cx);
            ch = Math.Min(ch, height - cy);

            if (cw > 0 && ch > 0)
            {
                var canvasDevice = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                using var canvasBitmap = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(
                    canvasDevice, pixels, width, height,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

                using var renderTarget = new Microsoft.Graphics.Canvas.CanvasRenderTarget(canvasDevice, cw, ch, 96);
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    ds.DrawImage(canvasBitmap, new Windows.Foundation.Rect(0, 0, cw, ch), new Windows.Foundation.Rect(cx, cy, cw, ch));
                }

                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var settings = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Services.SettingsManager>(((App)Application.Current).Services);
                if (settings.ImageFormat == "JPEG")
                {
                    float quality = Math.Clamp(settings.JpegQuality, 1, 100) / 100.0f;
                    await renderTarget.SaveAsync(ms, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Jpeg, quality);
                }
                else
                {
                    await renderTarget.SaveAsync(ms, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
                }

                var outBuffer = new byte[ms.Size];
                using (var dataReader = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0)))
                {
                    await dataReader.LoadAsync((uint)ms.Size);
                    dataReader.ReadBytes(outBuffer);
                }
                return outBuffer;
            }
            return Array.Empty<byte>();
        }

        // --- Drawing Logic ---
        private void StartDrawing(Windows.Foundation.Point p)
        {
            _isDrawing = true;
            _startPoint = p;
            
            if (_currentTool == EditorTool.Pen)
            {
                _currentPolyline = new Polyline { Stroke = _currentBrush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _currentPolyline.Points.Add(p);
                AnnotationCanvas.Children.Add(_currentPolyline);
                _undoStack.Push(_currentPolyline);
            }
            else if (_currentTool == EditorTool.Line)
            {
                _currentLine = new Microsoft.UI.Xaml.Shapes.Line { Stroke = _currentBrush, StrokeThickness = 4, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                AnnotationCanvas.Children.Add(_currentLine);
                _undoStack.Push(_currentLine);
            }
            else if (_currentTool == EditorTool.Arrow)
            {
                _currentArrow = new Microsoft.UI.Xaml.Shapes.Path { Stroke = _currentBrush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                UpdateArrowPath(p, p);
                AnnotationCanvas.Children.Add(_currentArrow);
                _undoStack.Push(_currentArrow);
            }
            else if (_currentTool == EditorTool.Rect)
            {
                _currentRect = new Microsoft.UI.Xaml.Shapes.Rectangle { StrokeThickness = 4 };
                if (_isFillEnabled) { _currentRect.Fill = _currentBrush; _currentRect.Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent); }
                else { _currentRect.Stroke = _currentBrush; _currentRect.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent); }
                Canvas.SetLeft(_currentRect, p.X); Canvas.SetTop(_currentRect, p.Y);
                AnnotationCanvas.Children.Add(_currentRect);
                _undoStack.Push(_currentRect);
            }
            else if (_currentTool == EditorTool.Number)
            {
                var grid = new Grid { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = _currentBrush };
                grid.Children.Add(new TextBlock { Text = _stepNumber.ToString(), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 });
                Canvas.SetLeft(grid, p.X - 16); Canvas.SetTop(grid, p.Y - 16);
                AnnotationCanvas.Children.Add(grid);
                _undoStack.Push(grid);
                _stepNumber++;
                _isDrawing = false;
            }
            UndoBtn.IsEnabled = true;
        }

        private void UpdateDrawing(Windows.Foundation.Point p)
        {
            if (_currentTool == EditorTool.Pen && _currentPolyline != null) _currentPolyline.Points.Add(p);
            else if (_currentTool == EditorTool.Line && _currentLine != null) { _currentLine.X2 = p.X; _currentLine.Y2 = p.Y; }
            else if (_currentTool == EditorTool.Arrow && _currentArrow != null) UpdateArrowPath(_startPoint, p);
            else if (_currentTool == EditorTool.Rect && _currentRect != null)
            {
                Canvas.SetLeft(_currentRect, Math.Min(_startPoint.X, p.X)); Canvas.SetTop(_currentRect, Math.Min(_startPoint.Y, p.Y));
                _currentRect.Width = Math.Abs(p.X - _startPoint.X); _currentRect.Height = Math.Abs(p.Y - _startPoint.Y);
            }
        }

        private void UpdateArrowPath(Windows.Foundation.Point p1, Windows.Foundation.Point p2)
        {
            double headLen = 15;
            double headAngle = Math.PI / 6;
            double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            var p3 = new Windows.Foundation.Point(p2.X - headLen * Math.Cos(angle - headAngle), p2.Y - headLen * Math.Sin(angle - headAngle));
            var p4 = new Windows.Foundation.Point(p2.X - headLen * Math.Cos(angle + headAngle), p2.Y - headLen * Math.Sin(angle + headAngle));
            var pg = new PathGeometry();
            var pf = new PathFigure { StartPoint = p1, IsClosed = false };
            pf.Segments.Add(new LineSegment { Point = p2 });
            pf.Segments.Add(new LineSegment { Point = p3 });
            var pf2 = new PathFigure { StartPoint = p2, IsClosed = false };
            pf2.Segments.Add(new LineSegment { Point = p4 });
            pg.Figures.Add(pf); pg.Figures.Add(pf2);
            if (_currentArrow != null)
            {
                _currentArrow.Data = pg;
            }
        }
    }
}
