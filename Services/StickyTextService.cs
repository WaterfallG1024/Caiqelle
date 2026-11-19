using Caiqelle.Views;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Caiqelle.Services
{
    public record StickyTextListEntry(StickyTextItemData Data, bool IsCurrentlyOpen);

    /// <summary>
    /// 複数表示可能な Sticky Text ウィンドウの生成・一覧管理・保存復元・個別削除を担うサービス
    /// </summary>
    public class StickyTextService
    {
        private readonly SettingsManager _settings;
        private readonly List<StickyTextWindow> _windows = new();
        private readonly HashSet<string> _deletingIds = new();
        private StickyTextListWindow? _listWindow;
        private int _spawnCounter = 0;
        private DispatcherQueueTimer? _saveTimer;

        public event Action? ItemsChanged;

        public StickyTextService(SettingsManager settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Sticky Text を開く（起動時復元設定がONかつ保存データありの場合は前回の状態を復元する）
        /// </summary>
        public void OpenStickyText()
        {
            if (_windows.Count == 0 &&
                _settings.IsStickyTextSaveEnabled &&
                _settings.IsStickyTextRestoreOnOpen &&
                _settings.SavedStickyTexts.Count > 0)
            {
                var targets = _settings.SavedStickyTexts.Where(x => x.IsOpen).ToList();
                if (targets.Count == 0)
                {
                    // すべて閉じていた状態から開いた場合は保存済みアイテムをすべて復元
                    targets = _settings.SavedStickyTexts.ToList();
                }

                foreach (var item in targets)
                {
                    item.IsOpen = true;
                    var restored = CreateWindowInternal(item);
                    restored.Activate();
                }
                _settings.SaveSettings();
                ItemsChanged?.Invoke();
                return;
            }

            CreateNewStickyText();
        }

        /// <summary>
        /// アプリ起動時の自動復元（前回表示中だった Sticky Text のみを復元する）
        /// </summary>
        public void RestoreSavedStickyTexts()
        {
            if (!_settings.IsStickyTextSaveEnabled || _settings.SavedStickyTexts.Count == 0 || _windows.Count > 0)
            {
                return;
            }

            var targets = _settings.SavedStickyTexts.Where(x => x.IsOpen).ToList();
            if (targets.Count == 0) return;

            foreach (var item in targets)
            {
                // アプリ起動時の自動復元は表示モードで開始し、ツールバーを非表示にする
                item.IsEditing = false;
                var restored = CreateWindowInternal(item);
                restored.Activate();
            }
            ItemsChanged?.Invoke();
        }

        /// <summary>
        /// 新しい Sticky Text ウィンドウを作成して表示する
        /// </summary>
        public StickyTextWindow CreateNewStickyText()
        {
            var window = CreateWindowInternal(null);
            window.Activate();
            window.FocusEditor();
            ScheduleSave();
            ItemsChanged?.Invoke();
            return window;
        }

        /// <summary>
        /// 一覧オーバーレイウィンドウを開く（既に開いている場合は前面に表示）
        /// </summary>
        public void OpenStickyTextList()
        {
            if (_listWindow == null)
            {
                _listWindow = new StickyTextListWindow(this);
                _listWindow.Closed += (s, e) => _listWindow = null;
            }
            _listWindow.ShowAndFocus();
        }

        public List<StickyTextListEntry> GetListEntries(out bool isSaveEnabled)
        {
            isSaveEnabled = _settings.IsStickyTextSaveEnabled;
            var openById = _windows.ToDictionary(w => w.ItemId, w => w);

            if (!isSaveEnabled)
            {
                return _windows
                    .Select(w => new StickyTextListEntry(w.ToItemData(isOpen: true), true))
                    .ToList();
            }

            var result = new List<StickyTextListEntry>();
            foreach (var saved in _settings.SavedStickyTexts)
            {
                if (openById.TryGetValue(saved.Id, out var win))
                {
                    result.Add(new StickyTextListEntry(win.ToItemData(isOpen: true), true));
                }
                else
                {
                    result.Add(new StickyTextListEntry(saved, false));
                }
            }

            // まだ保存タイマー待ちで SavedStickyTexts に未反映の表示中ウィンドウがあれば追加
            foreach (var win in _windows)
            {
                if (_settings.SavedStickyTexts.All(s => s.Id != win.ItemId))
                {
                    result.Add(new StickyTextListEntry(win.ToItemData(isOpen: true), true));
                }
            }

            return result;
        }

        public void ToggleOpenById(string id)
        {
            var existing = _windows.FirstOrDefault(w => w.ItemId == id);
            if (existing != null)
            {
                existing.Close();
                return;
            }

            var saved = _settings.SavedStickyTexts.FirstOrDefault(x => x.Id == id);
            if (saved != null)
            {
                saved.IsOpen = true;
                var restored = CreateWindowInternal(saved);
                restored.Activate();
                _settings.SaveSettings();
                ItemsChanged?.Invoke();
            }
        }

        public void DeleteById(string id)
        {
            _deletingIds.Add(id);
            _settings.SavedStickyTexts.RemoveAll(x => x.Id == id);
            _settings.SaveSettings();

            var existing = _windows.FirstOrDefault(w => w.ItemId == id);
            existing?.Close();

            ItemsChanged?.Invoke();
        }

        public void DeleteAllSaved()
        {
            foreach (var w in _windows.ToArray())
            {
                _deletingIds.Add(w.ItemId);
                w.Close();
            }

            _saveTimer?.Stop();
            _settings.SavedStickyTexts.Clear();
            _settings.SaveSettings();
            ItemsChanged?.Invoke();
        }

        private StickyTextWindow CreateWindowInternal(StickyTextItemData? savedData)
        {
            int offsetIndex = _spawnCounter++ % 6;
            var window = new StickyTextWindow(
                _settings.IsStickyTextStealthMode,
                offsetIndex * 28,
                savedData,
                _settings.IsStickyTextSaveEnabled);
            _windows.Add(window);

            window.StateChanged += ScheduleSave;
            window.DeleteRequested += w => DeleteById(w.ItemId);
            window.Closed += (s, e) => OnWindowClosed(window);
            return window;
        }

        private void OnWindowClosed(StickyTextWindow closedWindow)
        {
            closedWindow.StateChanged -= ScheduleSave;
            _windows.Remove(closedWindow);

            if (_deletingIds.Remove(closedWindow.ItemId))
            {
                ItemsChanged?.Invoke();
                return;
            }

            if (_settings.IsStickyTextSaveEnabled)
            {
                var closedData = closedWindow.ToItemData(isOpen: false);
                if (string.IsNullOrWhiteSpace(closedData.Text))
                {
                    // 空の新規メモを閉じた場合は保存リストに残さない
                    _settings.SavedStickyTexts.RemoveAll(x => x.Id == closedData.Id);
                }
                else
                {
                    UpsertSavedItem(closedData);
                }
                _settings.SaveSettings();
            }

            ItemsChanged?.Invoke();
        }

        private void ScheduleSave()
        {
            if (!_settings.IsStickyTextSaveEnabled || _windows.Count == 0) return;

            if (_saveTimer == null)
            {
                var dq = _windows[0].DispatcherQueue;
                if (dq == null)
                {
                    SaveAllStates();
                    return;
                }
                _saveTimer = dq.CreateTimer();
                _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
                _saveTimer.IsRepeating = false;
                _saveTimer.Tick += (s, e) => SaveAllStates();
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// 表示中のすべての Sticky Text の状態を保存リストへマージ保存する（非表示の保存項目は保持）
        /// </summary>
        public void SaveAllStates()
        {
            if (!_settings.IsStickyTextSaveEnabled) return;

            foreach (var w in _windows.ToArray())
            {
                UpsertSavedItem(w.ToItemData(isOpen: true));
            }
            _settings.SaveSettings();
            ItemsChanged?.Invoke();
        }

        private void UpsertSavedItem(StickyTextItemData data)
        {
            int index = _settings.SavedStickyTexts.FindIndex(x => x.Id == data.Id);
            if (index >= 0)
            {
                _settings.SavedStickyTexts[index] = data;
            }
            else
            {
                _settings.SavedStickyTexts.Add(data);
            }
        }

        /// <summary>
        /// 保存設定の有効／無効切り替え時に各ウィンドウの表示と保存データを同期する
        /// </summary>
        public void SetSaveEnabled(bool enabled)
        {
            foreach (var w in _windows.ToArray())
            {
                w.UpdateSaveModeVisibility(enabled);
            }

            if (enabled)
            {
                SaveAllStates();
            }
            else
            {
                ClearSavedStates();
            }
        }

        /// <summary>
        /// 保存されている Sticky Text データを消去する
        /// </summary>
        public void ClearSavedStates()
        {
            _saveTimer?.Stop();
            _settings.SavedStickyTexts.Clear();
            _settings.SaveSettings();
            ItemsChanged?.Invoke();
        }

        /// <summary>
        /// 表示中のすべての Sticky Text ウィンドウの共有／カンペモードを一括で切り替える
        /// </summary>
        public void SetAllStealthMode(bool isStealth)
        {
            foreach (var window in _windows.ToArray())
            {
                window.SetStealthMode(isStealth);
            }
        }
    }
}
