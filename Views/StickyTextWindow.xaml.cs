using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace Caiqelle.Views
{
    public sealed partial class StickyTextWindow : Window
    {
        private enum BackgroundStyle { SemiTransparent, Opaque, Transparent }

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly StickyTextToolbarWindow _toolbarWindow;
        private bool _isEditing = true;
        private bool _isStealth = false;
        private bool _isBold = false;
        private bool _isItalic = false;
        private bool _isUnderline = false;
        private BackgroundStyle _bgStyle = BackgroundStyle.SemiTransparent;
        private double _fontSize = 24.0;
        private Windows.UI.Color _textColor = Colors.White;
        private Brush? _cardDefaultBackground;
        private Brush? _editorDefaultBackground;

        // ウィンドウリサイズ操作用状態
        private bool _isResizing = false;
        private ResizeDirection _activeResizeDirection;
        private NativeMethods.POINT _resizeStartCursor;
        private Windows.Graphics.RectInt32 _resizeStartBounds;
        private bool _isRestoring = false;

        // 表示モード時のドラッグ移動判定用（ダブルクリックと両立させるため閾値移動後に開始）
        private bool _isPendingDrag = false;
        private NativeMethods.POINT _dragStartCursor;

        // 枠外ツールバーおよび左上編集ボタンの表示制御
        private bool _isPointerInCard = false;

        /// <summary>
        /// この Sticky Text インスタンスの一意な識別子
        /// </summary>
        public string ItemId { get; private set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// テキストや書式・位置・サイズが変更された際に発火する通知
        /// </summary>
        public event Action? StateChanged;

        /// <summary>
        /// 削除ボタンが押され、保存データからの完全削除が要求された際に発火する通知
        /// </summary>
        public event Action<StickyTextWindow>? DeleteRequested;

        private const double MinFontSize = 14.0;
        private const double MaxFontSize = 72.0;
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public StickyTextWindow(bool initialStealthMode, int spawnOffsetPx = 0, Services.StickyTextItemData? savedData = null, bool isSaveEnabled = false)
        {
            this.InitializeComponent();
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Colors.Transparent);

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            ExtendsContentIntoTitleBar = true;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = true;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            // ウィンドウの角丸・白枠線を除去
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);

            _cardDefaultBackground = CardBackgroundBorder.Background;
            _editorDefaultBackground = InputRichEditBox.Background;

            // テキスト選択時のミニツールバーが枠外ツールバーと重なるのを防ぐ
            InputRichEditBox.SelectionFlyout = null;

            // 表示モード（IsReadOnly）でもドラッグ移動とダブルクリック編集が機能するようハンドラを登録
            InputRichEditBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Editor_PointerPressed), true);
            InputRichEditBox.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Editor_PointerMoved), true);
            InputRichEditBox.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Editor_PointerReleased), true);
            InputRichEditBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(Editor_DoubleTapped), true);

            _toolbarWindow = new StickyTextToolbarWindow(this);

            // 初期サイズとカーソルディスプレイ中央付近への配置（または保存データの復元）
            double dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            UpdateSaveModeVisibility(isSaveEnabled);
            if (savedData != null)
            {
                ItemId = string.IsNullOrWhiteSpace(savedData.Id) ? Guid.NewGuid().ToString("N") : savedData.Id;
                RestoreFromData(savedData, dpiScale);
            }
            else
            {
                int width = (int)(440 * dpiScale);
                int height = (int)(180 * dpiScale);
                _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

                NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos);
                var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y), DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var work = displayArea.WorkArea;
                    int x = Math.Clamp(work.X + (work.Width - width) / 2 + spawnOffsetPx, work.X, work.X + Math.Max(0, work.Width - width));
                    int y = Math.Clamp(work.Y + (work.Height - height) / 2 + spawnOffsetPx, work.Y, work.Y + Math.Max(0, work.Height - height));
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                }

                SetStealthMode(initialStealthMode);
                ApplyTextFormatting(applyColorToAll: true);
                SetEditingMode(true);
            }

            SyncToolbarPosition();
            UpdateToolbarVisibility();

            _appWindow.Changed += (s, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange)
                {
                    SyncToolbarPosition();
                    NotifyStateChanged();
                }
            };

            this.Closed += (s, e) =>
            {
                _toolbarWindow.Close();
            };
        }

        private void SyncToolbarPosition()
        {
            var rect = new Windows.Graphics.RectInt32(
                _appWindow.Position.X,
                _appWindow.Position.Y,
                _appWindow.Size.Width,
                _appWindow.Size.Height);
            _toolbarWindow.PositionRelativeTo(rect);
        }

        private void UpdateToolbarVisibility()
        {
            if (_isEditing)
            {
                SyncToolbarPosition();
                _toolbarWindow.ShowNoActivate();
                HoverActionPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                _toolbarWindow.HideToolbar();
                HoverActionPanel.Visibility = _isPointerInCard ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void HoverEditBtn_Click(object sender, RoutedEventArgs e)
        {
            SetEditingMode(true);
        }

        private void HoverMoveHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(HoverMoveHandle).Properties.IsLeftButtonPressed)
            {
                _isPendingDrag = false;
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                e.Handled = true;
            }
        }

        private void NotifyStateChanged()
        {
            if (!_isRestoring)
            {
                StateChanged?.Invoke();
            }
        }

        private void RestoreFromData(Services.StickyTextItemData data, double dpiScale)
        {
            _isRestoring = true;
            try
            {
                int minW = (int)(180 * dpiScale);
                int minH = (int)(90 * dpiScale);
                int width = Math.Max(minW, data.Width > 0 ? data.Width : (int)(440 * dpiScale));
                int height = Math.Max(minH, data.Height > 0 ? data.Height : (int)(180 * dpiScale));

                var displayArea = DisplayArea.GetFromPoint(
                    new Windows.Graphics.PointInt32(data.X + width / 2, data.Y + height / 2),
                    DisplayAreaFallback.Primary);
                int x = data.X;
                int y = data.Y;
                if (displayArea != null)
                {
                    var work = displayArea.WorkArea;
                    x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
                    y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
                }
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

                _fontSize = Math.Clamp(data.FontSize, MinFontSize, MaxFontSize);
                _isBold = data.IsBold;
                _isItalic = data.IsItalic;
                _isUnderline = data.IsUnderline;
                _textColor = ParseColorHex(data.ColorHex);

                var bgStyle = data.BackgroundMode switch
                {
                    1 => BackgroundStyle.Opaque,
                    2 => BackgroundStyle.Transparent,
                    _ => BackgroundStyle.SemiTransparent
                };
                ApplyBackgroundStyle(bgStyle);

                if (!string.IsNullOrEmpty(data.RtfText))
                {
                    InputRichEditBox.Document.SetText(TextSetOptions.FormatRtf, data.RtfText);
                    _toolbarWindow.SyncFormatting(_isBold, _isItalic, _isUnderline, _fontSize, _textColor);
                }
                else
                {
                    InputRichEditBox.Document.SetText(TextSetOptions.None, data.Text ?? string.Empty);
                    ApplyTextFormatting(applyColorToAll: true);
                }

                SetStealthMode(data.IsStealth);
                SetEditingMode(data.IsEditing);
            }
            finally
            {
                _isRestoring = false;
            }
        }

        /// <summary>
        /// 保存設定の有効／無効に応じて削除ボタンの表示を切り替える
        /// </summary>
        public void UpdateSaveModeVisibility(bool isSaveEnabled)
        {
            _toolbarWindow.SyncSaveModeVisibility(isSaveEnabled);
        }

        private string GetEditorRtf()
        {
            InputRichEditBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            return rtf;
        }

        /// <summary>
        /// 現在の Sticky Text の状態をシリアライズ用データに変換する
        /// </summary>
        public Services.StickyTextItemData ToItemData(bool isOpen = true)
        {
            return new Services.StickyTextItemData
            {
                Id = ItemId,
                IsOpen = isOpen,
                Text = GetEditorText(),
                RtfText = GetEditorRtf(),
                X = _appWindow.Position.X,
                Y = _appWindow.Position.Y,
                Width = _appWindow.Size.Width,
                Height = _appWindow.Size.Height,
                FontSize = _fontSize,
                IsBold = _isBold,
                IsItalic = _isItalic,
                IsUnderline = _isUnderline,
                ColorHex = $"#{_textColor.A:X2}{_textColor.R:X2}{_textColor.G:X2}{_textColor.B:X2}",
                BackgroundMode = _bgStyle switch
                {
                    BackgroundStyle.Opaque => 1,
                    BackgroundStyle.Transparent => 2,
                    _ => 0
                },
                IsStealth = _isStealth,
                IsEditing = _isEditing
            };
        }

        private static Windows.UI.Color ParseColorHex(string? hex)
        {
            if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith('#') && hex.Length == 9 &&
                byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte a) &&
                byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(hex.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return Windows.UI.Color.FromArgb(a, r, g, b);
            }
            return Colors.White;
        }

        public void FocusEditor()
        {
            InputRichEditBox.Focus(FocusState.Programmatic);
        }

        // --- ツールバーウィンドウからの操作ハンドラ ---
        public void SetBold(bool value) { _isBold = value; ApplyTextFormatting(); }
        public void SetItalic(bool value) { _isItalic = value; ApplyTextFormatting(); }
        public void SetUnderline(bool value) { _isUnderline = value; ApplyTextFormatting(); }

        /// <summary>
        /// 選択中の文字、または現在の入力位置の文字色を変更する（文字ごとの色設定に対応）
        /// </summary>
        public void SetTextColor(Windows.UI.Color color)
        {
            _textColor = color;
            var sel = InputRichEditBox.Document.Selection;

            if (sel.Length != 0)
            {
                sel.CharacterFormat.ForegroundColor = color;
            }
            else
            {
                sel.CharacterFormat.ForegroundColor = color;
                var defaultFmt = InputRichEditBox.Document.GetDefaultCharacterFormat();
                defaultFmt.ForegroundColor = color;
                InputRichEditBox.Document.SetDefaultCharacterFormat(defaultFmt);
            }

            _toolbarWindow.SyncFormatting(_isBold, _isItalic, _isUnderline, _fontSize, _textColor);
            NotifyStateChanged();
        }

        public void AdjustFontSize(double delta)
        {
            _fontSize = Math.Clamp(_fontSize + delta, MinFontSize, MaxFontSize);
            ApplyTextFormatting();
        }

        public void CycleBackgroundStyle()
        {
            var nextStyle = _bgStyle switch
            {
                BackgroundStyle.SemiTransparent => BackgroundStyle.Opaque,
                BackgroundStyle.Opaque => BackgroundStyle.Transparent,
                _ => BackgroundStyle.SemiTransparent
            };
            ApplyBackgroundStyle(nextStyle);
        }

        public void ToggleEditingMode() => SetEditingMode(!_isEditing);

        public void RequestAddNew()
        {
            var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Services.StickyTextService>(((App)Application.Current).Services);
            service.CreateNewStickyText();
        }

        public void RequestOpenList()
        {
            var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Services.StickyTextService>(((App)Application.Current).Services);
            service.OpenStickyTextList();
        }

        public void RequestDelete() => DeleteRequested?.Invoke(this);

        /// <summary>
        /// 共有モード（false）と発表者用カンペモード（true）を切り替える
        /// </summary>
        public void SetStealthMode(bool isStealth)
        {
            _isStealth = isStealth;
            SetWindowDisplayAffinity(_hwnd, isStealth ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            StealthBadge.Visibility = isStealth ? Visibility.Visible : Visibility.Collapsed;
            _toolbarWindow.SyncStealth(isStealth);
            NotifyStateChanged();
        }

        private string GetEditorText()
        {
            InputRichEditBox.Document.GetText(TextGetOptions.None, out string raw);
            return raw.TrimEnd('\r', '\n');
        }

        private void SetEditingMode(bool editing)
        {
            _isEditing = editing;
            var transparent = new SolidColorBrush(Colors.Transparent);

            if (editing)
            {
                InputRichEditBox.IsReadOnly = false;
                InputRichEditBox.Background = _editorDefaultBackground;
                InputRichEditBox.Resources["TextControlBackgroundPointerOver"] = _editorDefaultBackground;
                InputRichEditBox.Resources["TextControlBackgroundFocused"] = _editorDefaultBackground;
                EmptyDisplayHint.Visibility = Visibility.Collapsed;
                InputRichEditBox.Focus(FocusState.Programmatic);
                InputRichEditBox.Document.Selection.SetRange(int.MaxValue, int.MaxValue);
            }
            else
            {
                InputRichEditBox.IsReadOnly = true;
                InputRichEditBox.Background = transparent;
                InputRichEditBox.Resources["TextControlBackgroundPointerOver"] = transparent;
                InputRichEditBox.Resources["TextControlBackgroundFocused"] = transparent;
                InputRichEditBox.Document.Selection.SetRange(0, 0);
                EmptyDisplayHint.Visibility = string.IsNullOrWhiteSpace(GetEditorText())
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            _toolbarWindow.SyncEditing(editing);
            UpdateToolbarVisibility();
            NotifyStateChanged();
        }

        private void ApplyTextFormatting(bool applyColorToAll = false)
        {
            var weight = _isBold ? FontWeights.Bold : FontWeights.Normal;
            var style = _isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

            InputRichEditBox.FontWeight = weight;
            InputRichEditBox.FontStyle = style;
            InputRichEditBox.FontSize = _fontSize;

            // RichEditBox 内の既存テキストへ太さ・斜体・下線・サイズを反映（文字ごとの色は保持する）
            float ptSize = (float)(_fontSize * 0.75);
            var underlineType = _isUnderline ? UnderlineType.Single : UnderlineType.None;
            var formatEffectBold = _isBold ? FormatEffect.On : FormatEffect.Off;
            var formatEffectItalic = _isItalic ? FormatEffect.On : FormatEffect.Off;

            var defaultFmt = InputRichEditBox.Document.GetDefaultCharacterFormat();
            defaultFmt.Bold = formatEffectBold;
            defaultFmt.Italic = formatEffectItalic;
            defaultFmt.Underline = underlineType;
            defaultFmt.Size = ptSize;
            defaultFmt.ForegroundColor = _textColor;
            InputRichEditBox.Document.SetDefaultCharacterFormat(defaultFmt);

            var allRange = InputRichEditBox.Document.GetRange(0, TextConstants.MaxUnitCount);
            allRange.CharacterFormat.Bold = formatEffectBold;
            allRange.CharacterFormat.Italic = formatEffectItalic;
            allRange.CharacterFormat.Underline = underlineType;
            allRange.CharacterFormat.Size = ptSize;
            if (applyColorToAll)
            {
                allRange.CharacterFormat.ForegroundColor = _textColor;
            }

            _toolbarWindow.SyncFormatting(_isBold, _isItalic, _isUnderline, _fontSize, _textColor);
            NotifyStateChanged();
        }

        private void InputRichEditBox_TextChanged(object sender, RoutedEventArgs e) => NotifyStateChanged();

        private void InputRichEditBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (!_isEditing || _isRestoring) return;
            var fg = InputRichEditBox.Document.Selection.CharacterFormat.ForegroundColor;
            if (fg.A > 0)
            {
                _textColor = fg;
                _toolbarWindow.SyncFormatting(_isBold, _isItalic, _isUnderline, _fontSize, _textColor);
            }
        }

        private void ApplyBackgroundStyle(BackgroundStyle style)
        {
            _bgStyle = style;
            switch (_bgStyle)
            {
                case BackgroundStyle.SemiTransparent:
                    CardBackgroundBorder.Background = _cardDefaultBackground;
                    CardBackgroundBorder.BorderThickness = new Thickness(1);
                    _toolbarWindow.SyncBackgroundLabel("半透明", "背景: 半透明（クリックで不透明へ）");
                    break;
                case BackgroundStyle.Opaque:
                    CardBackgroundBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 22, 26));
                    CardBackgroundBorder.BorderThickness = new Thickness(1);
                    _toolbarWindow.SyncBackgroundLabel("不透明", "背景: 不透明（クリックで透過へ）");
                    break;
                case BackgroundStyle.Transparent:
                    // ヒットテストとドラッグ移動を維持するため極小アルファの透明色を使用
                    CardBackgroundBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
                    CardBackgroundBorder.BorderThickness = new Thickness(0);
                    _toolbarWindow.SyncBackgroundLabel("透過", "背景: 透過（クリックで半透明へ）");
                    break;
            }
            NotifyStateChanged();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInCard = true;
            UpdateToolbarVisibility();
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInCard = false;
            UpdateToolbarVisibility();
        }

        private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!_isEditing)
            {
                SetEditingMode(true);
            }
        }

        private void Editor_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!_isEditing)
            {
                _isPendingDrag = false;
                SetEditingMode(true);
                e.Handled = true;
            }
        }

        private void Editor_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isEditing) return;
            if (e.GetCurrentPoint(InputRichEditBox).Properties.IsLeftButtonPressed)
            {
                _isPendingDrag = true;
                NativeMethods.GetCursorPos(out _dragStartCursor);
            }
        }

        private void Editor_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPendingDrag || _isEditing) return;
            if (!e.GetCurrentPoint(InputRichEditBox).Properties.IsLeftButtonPressed)
            {
                _isPendingDrag = false;
                return;
            }

            NativeMethods.GetCursorPos(out NativeMethods.POINT current);
            if (Math.Abs(current.X - _dragStartCursor.X) >= 4 || Math.Abs(current.Y - _dragStartCursor.Y) >= 4)
            {
                _isPendingDrag = false;
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        private void Editor_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isPendingDrag = false;
        }

        private void InputRichEditBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool isCtrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (isCtrl && e.Key == VirtualKey.Enter)
            {
                SetEditingMode(false);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                if (!string.IsNullOrWhiteSpace(GetEditorText()))
                {
                    SetEditingMode(false);
                }
                else
                {
                    this.Close();
                }
                e.Handled = true;
            }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape && !_isEditing)
            {
                this.Close();
                e.Handled = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isEditing) return;
            if (e.OriginalSource is Grid || e.OriginalSource is Border || e.OriginalSource is TextBlock)
            {
                ReleaseCapture();
                SendMessage(_hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not ResizeGrip grip) return;
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            _isResizing = true;
            _activeResizeDirection = grip.Direction;
            NativeMethods.GetCursorPos(out _resizeStartCursor);
            _resizeStartBounds = new Windows.Graphics.RectInt32(
                _appWindow.Position.X,
                _appWindow.Position.Y,
                _appWindow.Size.Width,
                _appWindow.Size.Height);

            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing) return;

            NativeMethods.GetCursorPos(out NativeMethods.POINT current);
            int dx = current.X - _resizeStartCursor.X;
            int dy = current.Y - _resizeStartCursor.Y;

            double dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            int minW = (int)(180 * dpiScale);
            int minH = (int)(90 * dpiScale);

            int newX = _resizeStartBounds.X;
            int newY = _resizeStartBounds.Y;
            int newW = _resizeStartBounds.Width;
            int newH = _resizeStartBounds.Height;

            if (_activeResizeDirection is ResizeDirection.Left or ResizeDirection.TopLeft or ResizeDirection.BottomLeft)
            {
                int candidateW = Math.Max(minW, _resizeStartBounds.Width - dx);
                newX = _resizeStartBounds.X + (_resizeStartBounds.Width - candidateW);
                newW = candidateW;
            }
            else if (_activeResizeDirection is ResizeDirection.Right or ResizeDirection.TopRight or ResizeDirection.BottomRight)
            {
                newW = Math.Max(minW, _resizeStartBounds.Width + dx);
            }

            if (_activeResizeDirection is ResizeDirection.Top or ResizeDirection.TopLeft or ResizeDirection.TopRight)
            {
                int candidateH = Math.Max(minH, _resizeStartBounds.Height - dy);
                newY = _resizeStartBounds.Y + (_resizeStartBounds.Height - candidateH);
                newH = candidateH;
            }
            else if (_activeResizeDirection is ResizeDirection.Bottom or ResizeDirection.BottomLeft or ResizeDirection.BottomRight)
            {
                newH = Math.Max(minH, _resizeStartBounds.Height + dy);
            }

            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(newX, newY, newW, newH));
            e.Handled = true;
        }

        private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing) return;
            _isResizing = false;
            if (sender is UIElement el) el.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isResizing = false;
        }
    }

    public enum ResizeDirection
    {
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>
    /// ウィンドウ端のリサイズ用カーソルを自動で表示する透明ヒット領域
    /// </summary>
    public sealed class ResizeGrip : Grid
    {
        private ResizeDirection _direction;

        public ResizeDirection Direction
        {
            get => _direction;
            set
            {
                _direction = value;
                ProtectedCursor = InputSystemCursor.Create(value switch
                {
                    ResizeDirection.Left or ResizeDirection.Right => InputSystemCursorShape.SizeWestEast,
                    ResizeDirection.Top or ResizeDirection.Bottom => InputSystemCursorShape.SizeNorthSouth,
                    ResizeDirection.TopLeft or ResizeDirection.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
                    ResizeDirection.TopRight or ResizeDirection.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                });
            }
        }
    }
}
