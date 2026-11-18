using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace Caiqelle.ViewModels
{
    /// <summary>
    /// エディターで使用する描画ツール種別
    /// </summary>
    public enum EditorTool
    {
        Pen,
        Line,
        DashedLine,
        Arrow,
        Rect,
        Ellipse,
        Number,
        Erase
    }

    /// <summary>
    /// EditorWindow のツール選択・色・塗りつぶし・Undo/Redo 状態を管理する ViewModel
    /// </summary>
    public partial class EditorViewModel : ObservableObject
    {
        public Action? RequestUndo { get; set; }
        public Action? RequestRedo { get; set; }
        public Action? RequestClear { get; set; }
        public Func<Task>? RequestCopyAsync { get; set; }
        public Func<Task>? RequestSaveAsync { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPenSelected))]
        [NotifyPropertyChangedFor(nameof(IsLineSelected))]
        [NotifyPropertyChangedFor(nameof(IsDashedLineSelected))]
        [NotifyPropertyChangedFor(nameof(IsArrowSelected))]
        [NotifyPropertyChangedFor(nameof(IsRectSelected))]
        [NotifyPropertyChangedFor(nameof(IsEllipseSelected))]
        [NotifyPropertyChangedFor(nameof(IsNumberSelected))]
        [NotifyPropertyChangedFor(nameof(IsEraseSelected))]
        [NotifyPropertyChangedFor(nameof(IsShapeSelected))]
        [NotifyPropertyChangedFor(nameof(IsFillCapable))]
        public partial EditorTool CurrentTool { get; set; } = EditorTool.Rect;

        private EditorTool _lastShapeTool = EditorTool.Rect;

        public bool IsPenSelected => CurrentTool == EditorTool.Pen;
        public bool IsLineSelected => CurrentTool == EditorTool.Line;
        public bool IsDashedLineSelected => CurrentTool == EditorTool.DashedLine;
        public bool IsArrowSelected => CurrentTool == EditorTool.Arrow;
        public bool IsRectSelected => CurrentTool == EditorTool.Rect;
        public bool IsEllipseSelected => CurrentTool == EditorTool.Ellipse;
        public bool IsNumberSelected => CurrentTool == EditorTool.Number;
        public bool IsEraseSelected => CurrentTool == EditorTool.Erase;
        public bool IsShapeSelected => CurrentTool is EditorTool.Line or EditorTool.DashedLine or EditorTool.Arrow or EditorTool.Rect or EditorTool.Ellipse;
        public bool IsFillCapable => CurrentTool is EditorTool.Rect or EditorTool.Ellipse;

        [ObservableProperty]
        public partial bool IsFillEnabled { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PenThicknessText))]
        public partial double PenThickness { get; set; } = 4.0;

        public string PenThicknessText => $"{(int)Math.Round(PenThickness)}px";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedColorBrush))]
        public partial Color SelectedColor { get; set; } = Colors.Red;

        public SolidColorBrush SelectedColorBrush => new(SelectedColor);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
        public partial bool CanUndo { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
        public partial bool CanRedo { get; set; }

        [RelayCommand]
        private void SelectTool(string? toolName)
        {
            if (toolName == "Shapes")
            {
                CurrentTool = _lastShapeTool;
                return;
            }

            if (Enum.TryParse<EditorTool>(toolName, out var tool))
            {
                CurrentTool = tool;
                if (tool is EditorTool.Line or EditorTool.DashedLine or EditorTool.Arrow or EditorTool.Rect or EditorTool.Ellipse)
                {
                    _lastShapeTool = tool;
                }
            }
        }

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo() => RequestUndo?.Invoke();

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo() => RequestRedo?.Invoke();

        [RelayCommand]
        private void Clear() => RequestClear?.Invoke();

        [RelayCommand]
        private Task CopyAsync() => RequestCopyAsync?.Invoke() ?? Task.CompletedTask;

        [RelayCommand]
        private Task SaveAsync() => RequestSaveAsync?.Invoke() ?? Task.CompletedTask;
    }
}
