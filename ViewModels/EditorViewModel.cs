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
        Arrow,
        Rect,
        Number
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
        [NotifyPropertyChangedFor(nameof(IsArrowSelected))]
        [NotifyPropertyChangedFor(nameof(IsRectSelected))]
        [NotifyPropertyChangedFor(nameof(IsNumberSelected))]
        public partial EditorTool CurrentTool { get; set; } = EditorTool.Pen;

        public bool IsPenSelected => CurrentTool == EditorTool.Pen;
        public bool IsLineSelected => CurrentTool == EditorTool.Line;
        public bool IsArrowSelected => CurrentTool == EditorTool.Arrow;
        public bool IsRectSelected => CurrentTool == EditorTool.Rect;
        public bool IsNumberSelected => CurrentTool == EditorTool.Number;

        [ObservableProperty]
        public partial bool IsFillEnabled { get; set; }

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
            if (Enum.TryParse<EditorTool>(toolName, out var tool))
            {
                CurrentTool = tool;
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
