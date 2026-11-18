using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Caiqelle.Services;

namespace Caiqelle.ViewModels
{
    /// <summary>
    /// FolderSelectionWindow の状態・ページング・画像保存を管理する ViewModel
    /// </summary>
    public partial class FolderSelectionViewModel : ObservableObject
    {
        private readonly SettingsManager _settings;
        private readonly NotificationService _notificationService;
        private readonly List<FolderMapping> _allMappings = new();

        private int _currentPage = 1;
        private int _totalPages = 1;
        private byte[]? _imageBytes;

        /// <summary>
        /// 保存完了またはキャンセル時にウィンドウを閉じるためのコールバック
        /// </summary>
        public Action<bool>? RequestCloseWithResult { get; set; }

        public ObservableCollection<FolderMapping> CurrentPageMappings { get; } = new();

        [ObservableProperty]
        public partial string FileNameInput { get; set; } = string.Empty;

        partial void OnFileNameInputChanged(string value)
        {
            if (IsErrorState)
            {
                IsErrorState = false;
                PageIndicatorText = $"{_currentPage}/{_totalPages}";
            }
        }

        [ObservableProperty]
        public partial string PageIndicatorText { get; set; } = "1/1";

        [ObservableProperty]
        public partial bool IsPrevButtonVisible { get; set; }

        [ObservableProperty]
        public partial bool IsNextButtonVisible { get; set; }

        [ObservableProperty]
        public partial bool IsErrorState { get; set; }

        /// <summary>
        /// フォルダ作成完了後にダミーボタンへフォーカスを戻すコールバック（キー保存をすぐ使えるようにする）
        /// </summary>
        public Action? RequestRestoreDummyFocus { get; set; }

        [ObservableProperty]
        public partial string NewFolderName { get; set; } = string.Empty;

        partial void OnNewFolderNameChanged(string value)
        {
            IsCreateErrorOpen = false;
        }

        [ObservableProperty]
        public partial string NewFolderKey { get; set; } = string.Empty;

        partial void OnNewFolderKeyChanged(string value)
        {
            IsCreateErrorOpen = false;
        }

        [ObservableProperty]
        public partial bool IsCreateErrorOpen { get; set; }

        [ObservableProperty]
        public partial string CreateErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsSaving { get; set; }

        public FolderSelectionViewModel(SettingsManager settings, NotificationService notificationService)
        {
            _settings = settings;
            _notificationService = notificationService;
            ReloadMappings();
        }

        /// <summary>
        /// 表示前の初期化（対象画像データをセットしてマッピングを再読み込み）
        /// </summary>
        public void PrepareForShow(byte[] imageBytes)
        {
            _imageBytes = imageBytes;
            FileNameInput = string.Empty;
            NewFolderName = string.Empty;
            NewFolderKey = string.Empty;
            IsCreateErrorOpen = false;
            CreateErrorMessage = string.Empty;
            IsErrorState = false;
            IsSaving = false;
            ReloadMappings();
        }

        [RelayCommand]
        private void CreateFolder()
        {
            IsCreateErrorOpen = false;

            if (!_settings.TryAddFolderMapping(NewFolderName, NewFolderKey, createDirectory: true, out string errorMessage))
            {
                CreateErrorMessage = errorMessage;
                IsCreateErrorOpen = true;
                return;
            }

            NewFolderName = string.Empty;
            NewFolderKey = string.Empty;

            // 新規作成したフォルダが見える最終ページを表示
            _allMappings.Clear();
            _allMappings.AddRange(_settings.ImageFolderMappings);
            _totalPages = Math.Max(1, (int)Math.Ceiling(_allMappings.Count / 10.0));
            LoadPage(_totalPages);

            _notificationService.ShowNotification("フォルダとキーを追加しました", "\uE8F4");
            RequestRestoreDummyFocus?.Invoke();
        }

        public void ReloadMappings()
        {
            _allMappings.Clear();
            _allMappings.AddRange(_settings.ImageFolderMappings);
            _totalPages = Math.Max(1, (int)Math.Ceiling(_allMappings.Count / 10.0));
            LoadPage(1);
        }

        private void LoadPage(int page)
        {
            _currentPage = page;
            CurrentPageMappings.Clear();

            int startIndex = (page - 1) * 10;
            int count = Math.Min(10, _allMappings.Count - startIndex);
            for (int i = 0; i < count; i++)
            {
                CurrentPageMappings.Add(_allMappings[startIndex + i]);
            }

            PageIndicatorText = $"{_currentPage}/{_totalPages}";
            IsPrevButtonVisible = _currentPage > 1;
            IsNextButtonVisible = _currentPage < _totalPages;
            IsErrorState = false;
        }

        [RelayCommand]
        private void PreviousPage()
        {
            if (_currentPage > 1)
            {
                LoadPage(_currentPage - 1);
            }
        }

        [RelayCommand]
        private void NextPage()
        {
            if (_currentPage < _totalPages)
            {
                LoadPage(_currentPage + 1);
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            RequestCloseWithResult?.Invoke(false);
        }

        /// <summary>
        /// 現在ページのキー割り当てと一致する場合に保存を実行する
        /// </summary>
        public bool TrySelectByKey(string keyStr)
        {
            if (IsSaving || string.IsNullOrWhiteSpace(keyStr)) return false;

            foreach (var mapping in CurrentPageMappings)
            {
                if (string.Equals(mapping.Key, keyStr, StringComparison.OrdinalIgnoreCase))
                {
                    _ = SelectFolderAsync(mapping);
                    return true;
                }
            }
            return false;
        }

        [RelayCommand]
        private async Task SelectFolderAsync(FolderMapping? mapping)
        {
            if (mapping == null || IsSaving) return;

            if (_imageBytes == null || _imageBytes.Length == 0)
            {
                RequestCloseWithResult?.Invoke(false);
                return;
            }

            IsSaving = true;
            IsErrorState = false;
            PageIndicatorText = "保存中...";

            string folderPath = mapping.FolderName;
            string inputName = FileNameInput.Trim();
            byte[] imageBytes = _imageBytes;

            try
            {
                bool saved = await Task.Run(() =>
                {
                    string finalFolderPath = folderPath;
                    if (!Path.IsPathRooted(finalFolderPath))
                    {
                        string myPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        finalFolderPath = Path.Combine(myPictures, "Caiqelle", folderPath);
                    }

                    if (!Directory.Exists(finalFolderPath))
                    {
                        Directory.CreateDirectory(finalFolderPath);
                    }

                    // バイト列のヘッダーから実際の画像形式（JPEG / PNG）を判定し、中身と拡張子の不一致を防ぐ
                    bool isJpeg = imageBytes.Length >= 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8;
                    string defaultExt = isJpeg ? ".jpg" : ".png";

                    string fileName;
                    if (!string.IsNullOrEmpty(inputName))
                    {
                        foreach (char c in Path.GetInvalidFileNameChars())
                        {
                            inputName = inputName.Replace(c, '_');
                        }

                        bool hasMatchingExt = isJpeg
                            ? (inputName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || inputName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                            : inputName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                        fileName = hasMatchingExt ? inputName : $"{inputName}{defaultExt}";
                    }
                    else
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        fileName = $"Caiqelle_{timestamp}{defaultExt}";
                    }

                    string fullPath = Path.Combine(finalFolderPath, fileName);
                    if (File.Exists(fullPath))
                    {
                        return false;
                    }

                    File.WriteAllBytes(fullPath, imageBytes);
                    return true;
                });

                if (!saved)
                {
                    IsErrorState = true;
                    PageIndicatorText = "同名のファイルが既に存在します";
                    return;
                }

                _notificationService.ShowNotification($"{folderPath} に保存しました", "\uE8D5");
                RequestCloseWithResult?.Invoke(true);
            }
            catch (Exception ex)
            {
                IsErrorState = true;
                PageIndicatorText = $"エラー: {ex.Message}";
            }
            finally
            {
                IsSaving = false;
            }
        }
    }
}
