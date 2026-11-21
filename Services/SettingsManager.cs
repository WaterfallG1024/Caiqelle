using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Caiqelle.Services
{
    public class FolderMapping
    {
        public string FolderName { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayName => string.IsNullOrEmpty(Key) ? FolderName : $"{FolderName}({Key})";

        [System.Text.Json.Serialization.JsonIgnore]
        public string FormattedKey => string.IsNullOrEmpty(Key) ? "" : $"({Key})";
    }

    public class StickyTextItemData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public bool IsOpen { get; set; } = true;
        public string Text { get; set; } = string.Empty;
        public string? RtfText { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FontSize { get; set; } = 24.0;
        public bool IsBold { get; set; } = false;
        public bool IsItalic { get; set; } = false;
        public bool IsUnderline { get; set; } = false;
        public string ColorHex { get; set; } = "#FFFFFFFF";
        public int BackgroundMode { get; set; } = 0; // 0: 半透明, 1: 不透明, 2: 透過
        public bool IsStealth { get; set; } = false;
        public bool IsEditing { get; set; } = false;
    }

    public class ActionFieldMapping
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Keywords { get; set; } = string.Empty;
        public string EncryptedValue { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public string PlainValue
        {
            get => DpapiHelper.Unprotect(EncryptedValue);
            set => EncryptedValue = DpapiHelper.Protect(value);
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string MaskedValue => DpapiHelper.MaskValue(PlainValue);
    }

    public class SettingsData
    {
        public List<FolderMapping> ImageFolderMappings { get; set; } = new();
        public bool IsRewindEnabled { get; set; } = false;
        public bool IsRunAtStartup { get; set; } = true;
        public int RewindDurationSeconds { get; set; } = 30; // Default 30s
        public string RewindQuality { get; set; } = "FulHD"; // Default FulHD
        public string ImageFormat { get; set; } = "PNG"; // PNG, JPEG, WEBP
        public int JpegQuality { get; set; } = 100; // 1-100
        public bool IsHijackPrintScreen { get; set; } = true;
        public bool IsHijackWinShiftS { get; set; } = true;
        public bool IsAutoCopyToClipboard { get; set; } = false;
        public bool IsIgnoreOutsideSelectionClick { get; set; } = true;
        public bool IsStickyTextStealthMode { get; set; } = false;
        public bool IsStickyTextSaveEnabled { get; set; } = false;
        public bool IsStickyTextRestoreOnOpen { get; set; } = true;
        public List<StickyTextItemData> SavedStickyTexts { get; set; } = new();
        public bool IsShiftCapsLockRewindEnabled { get; set; } = false;
        public bool IsRewindTileCompressionEnabled { get; set; } = true;
        public bool IsAutoUpdateEnabled { get; set; } = true;
        public string LastDismissedReleaseTag { get; set; } = string.Empty;
        public AccentTheme AccentTheme { get; set; } = AccentTheme.Emerald;
        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.System;
        public bool IsActionAutoDetectEnabled { get; set; } = false;
        public bool IsActionOnlyOnBrowsers { get; set; } = true;
        public bool ActionRestoreClipboardAfterFill { get; set; } = true;
        public List<ActionFieldMapping> ActionMappings { get; set; } = new();
    }

    public class SettingsManager
    {
        private readonly string _settingsFilePath;
        private readonly object _saveLock = new();
        private const long MaxSettingsFileBytes = 10 * 1024 * 1024; // 10MB 上限
        public const int MaxFolderMappingCount = 44; // 0-9, A-Z, /[]-^;:@ の合計44キー
        private SettingsData _data;

        public List<FolderMapping> ImageFolderMappings => _data.ImageFolderMappings;
        
        public bool IsRewindEnabled 
        { 
            get => _data.IsRewindEnabled; 
            set => _data.IsRewindEnabled = value; 
        }

        public bool IsRunAtStartup
        {
            get => _data.IsRunAtStartup;
            set => _data.IsRunAtStartup = value;
        }

        public int RewindDurationSeconds 
        { 
            get => _data.RewindDurationSeconds; 
            set => _data.RewindDurationSeconds = value; 
        }
        
        public string RewindQuality 
        { 
            get => _data.RewindQuality; 
            set => _data.RewindQuality = value; 
        }

        public string ImageFormat
        {
            get => _data.ImageFormat;
            set => _data.ImageFormat = value;
        }

        public int JpegQuality
        {
            get => _data.JpegQuality;
            set => _data.JpegQuality = value;
        }

        public bool IsHijackPrintScreen
        {
            get => _data.IsHijackPrintScreen;
            set => _data.IsHijackPrintScreen = value;
        }

        public bool IsHijackWinShiftS
        {
            get => _data.IsHijackWinShiftS;
            set => _data.IsHijackWinShiftS = value;
        }

        public bool IsAutoCopyToClipboard
        {
            get => _data.IsAutoCopyToClipboard;
            set => _data.IsAutoCopyToClipboard = value;
        }

        public bool IsIgnoreOutsideSelectionClick
        {
            get => _data.IsIgnoreOutsideSelectionClick;
            set => _data.IsIgnoreOutsideSelectionClick = value;
        }

        public bool IsStickyTextStealthMode
        {
            get => _data.IsStickyTextStealthMode;
            set => _data.IsStickyTextStealthMode = value;
        }

        public bool IsStickyTextSaveEnabled
        {
            get => _data.IsStickyTextSaveEnabled;
            set => _data.IsStickyTextSaveEnabled = value;
        }

        public bool IsStickyTextRestoreOnOpen
        {
            get => _data.IsStickyTextRestoreOnOpen;
            set => _data.IsStickyTextRestoreOnOpen = value;
        }

        public List<StickyTextItemData> SavedStickyTexts => _data.SavedStickyTexts;

        public bool IsShiftCapsLockRewindEnabled
        {
            get => _data.IsShiftCapsLockRewindEnabled;
            set => _data.IsShiftCapsLockRewindEnabled = value;
        }

        public bool IsRewindTileCompressionEnabled
        {
            get => _data.IsRewindTileCompressionEnabled;
            set => _data.IsRewindTileCompressionEnabled = value;
        }

        public bool IsAutoUpdateEnabled
        {
            get => _data.IsAutoUpdateEnabled;
            set => _data.IsAutoUpdateEnabled = value;
        }

        public string LastDismissedReleaseTag
        {
            get => _data.LastDismissedReleaseTag;
            set => _data.LastDismissedReleaseTag = value;
        }

        public AccentTheme AccentTheme
        {
            get => _data.AccentTheme;
            set => _data.AccentTheme = value;
        }

        public AppThemeMode AppThemeMode
        {
            get => _data.AppThemeMode;
            set => _data.AppThemeMode = value;
        }

        public bool IsActionAutoDetectEnabled
        {
            get => _data.IsActionAutoDetectEnabled;
            set => _data.IsActionAutoDetectEnabled = value;
        }

        public bool IsActionOnlyOnBrowsers
        {
            get => _data.IsActionOnlyOnBrowsers;
            set => _data.IsActionOnlyOnBrowsers = value;
        }

        public bool ActionRestoreClipboardAfterFill
        {
            get => _data.ActionRestoreClipboardAfterFill;
            set => _data.ActionRestoreClipboardAfterFill = value;
        }

        public List<ActionFieldMapping> ActionMappings => _data.ActionMappings;

        [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
        public SettingsManager() : this(null)
        {
        }

        public SettingsManager(string? settingsDirectory)
        {
            string appFolder = string.IsNullOrWhiteSpace(settingsDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caiqelle")
                : settingsDirectory;
            Directory.CreateDirectory(appFolder);
            _settingsFilePath = Path.Combine(appFolder, "settings.json");
            
            _data = new SettingsData();
            LoadSettings();
            
            bool needSave = false;

            // 画像用フォルダ設定のデフォルト
            if (_data.ImageFolderMappings.Count == 0)
            {
                _data.ImageFolderMappings.Add(new FolderMapping { Key = "@", FolderName = "default_shot" });
                needSave = true;
            }

            if (needSave)
            {
                SaveSettings();
            }
        }

        public void LoadSettings()
        {
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var fileInfo = new FileInfo(_settingsFilePath);
                    if (fileInfo.Length > MaxSettingsFileBytes)
                    {
                        System.Diagnostics.Debug.WriteLine($"Settings file exceeds maximum allowed size ({fileInfo.Length} bytes).");
                        return;
                    }

                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<SettingsData>(json);
                    if (loaded != null)
                    {
                        _data = loaded;
                        foreach (var item in _data.SavedStickyTexts)
                        {
                            if (string.IsNullOrWhiteSpace(item.Id))
                            {
                                item.Id = Guid.NewGuid().ToString("N");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                }
            }
        }

        public event Action? MappingsChanged;

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// 単一のファイル名・フォルダ名として安全に使用できるか検証する
        /// </summary>
        public static bool IsSafeEntryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            if (trimmed == "." || trimmed == ".." || trimmed.EndsWith('.')) return false;
            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            string baseName = Path.GetFileNameWithoutExtension(trimmed);
            return !ReservedDeviceNames.Contains(trimmed) && !ReservedDeviceNames.Contains(baseName);
        }

        /// <summary>
        /// 指定パスが基準ディレクトリ配下に収まっているか検証する
        /// </summary>
        public static bool IsPathWithinDirectory(string baseDir, string candidatePath)
        {
            string normalizedBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            string normalizedTarget = Path.GetFullPath(candidatePath);
            return normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveFolderPath(string folderName)
        {
            string myPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string baseDir = Path.Combine(myPictures, "Caiqelle");

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return baseDir;
            }

            // 設定改ざん等による任意絶対パスへの書き込みを防止し、常に Pictures\Caiqelle 配下に限定
            string entryName = Path.GetFileName(folderName.Trim());
            if (!IsSafeEntryName(entryName))
            {
                return baseDir;
            }

            string resolved = Path.Combine(baseDir, entryName);
            return IsPathWithinDirectory(baseDir, resolved) ? resolved : baseDir;
        }

        private bool TryValidateFolderAndKey(
            string folderInput,
            string keyInput,
            FolderMapping? excludingMapping,
            out string folder,
            out string key,
            out string errorMessage)
        {
            errorMessage = string.Empty;
            folder = folderInput.Trim();
            key = keyInput.Trim().ToUpper();

            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(key))
            {
                errorMessage = "フォルダ名とキーを入力してください。";
                return false;
            }

            char[] invalidChars = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            if (folder.IndexOfAny(invalidChars) >= 0)
            {
                errorMessage = "フォルダ名に \\ / : * ? \" < > | は使用できません。";
                return false;
            }

            if (!IsSafeEntryName(folder))
            {
                errorMessage = "そのフォルダ名はシステム予約名または無効な形式のため使用できません。";
                return false;
            }

            if (key.Length != 1)
            {
                errorMessage = "キーは1文字のみ指定できます。";
                return false;
            }

            char k = key[0];
            bool isAlphaNumeric = (k >= '0' && k <= '9') || (k >= 'A' && k <= 'Z');
            bool isAllowedSymbol = "/[]-^;:@".Contains(k);
            if (!isAlphaNumeric && !isAllowedSymbol)
            {
                errorMessage = "キーには半角英数字(0-9, A-Z)または / [ ] - ^ ; : @ のみが使用可能です。";
                return false;
            }

            bool folderExists = false;
            bool keyExists = false;
            string resolvedFolder = ResolveFolderPath(folder);

            foreach (var m in _data.ImageFolderMappings)
            {
                if (ReferenceEquals(m, excludingMapping)) continue;

                if (string.Equals(m.FolderName, folder, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ResolveFolderPath(m.FolderName), resolvedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    folderExists = true;
                }
                if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    keyExists = true;
                }
            }

            if (folderExists && keyExists)
            {
                errorMessage = $"フォルダ名 '{folder}' とキー '{key}' はすでに登録されています。";
                return false;
            }
            if (folderExists)
            {
                errorMessage = $"フォルダ名 '{folder}' はすでに登録されています。";
                return false;
            }
            if (keyExists)
            {
                errorMessage = $"キー '{key}' はすでに登録されています。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// フォルダ名とキーを検証してマッピングを追加し、実フォルダを作成する
        /// </summary>
        public bool TryAddFolderMapping(string folderInput, string keyInput, bool createDirectory, out string errorMessage)
        {
            if (_data.ImageFolderMappings.Count >= MaxFolderMappingCount)
            {
                errorMessage = $"登録できるフォルダの上限（{MaxFolderMappingCount}件）に達しています。";
                return false;
            }

            if (!TryValidateFolderAndKey(folderInput, keyInput, null, out string folder, out string key, out errorMessage))
            {
                return false;
            }

            if (createDirectory)
            {
                try
                {
                    string targetPath = ResolveFolderPath(folder);
                    if (!Directory.Exists(targetPath))
                    {
                        Directory.CreateDirectory(targetPath);
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = $"フォルダの作成に失敗しました: {ex.Message}";
                    return false;
                }
            }

            _data.ImageFolderMappings.Add(new FolderMapping { FolderName = folder, Key = key });
            SaveSettings();
            MappingsChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// 既存のフォルダマッピングを検証して更新し、実フォルダが存在する場合はフォルダ名も変更する
        /// </summary>
        public bool TryUpdateFolderMapping(FolderMapping target, string folderInput, string keyInput, out string errorMessage)
        {
            if (target == null)
            {
                errorMessage = "編集対象のフォルダが見つかりません。";
                return false;
            }

            if (!TryValidateFolderAndKey(folderInput, keyInput, target, out string newFolder, out string newKey, out errorMessage))
            {
                return false;
            }

            string oldPath = ResolveFolderPath(target.FolderName);
            string newPath = ResolveFolderPath(newFolder);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(oldPath))
            {
                if (Directory.Exists(newPath))
                {
                    errorMessage = $"変更先のフォルダ '{newFolder}' が既に存在するため変更できません。";
                    return false;
                }

                try
                {
                    Directory.Move(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    errorMessage = $"フォルダ名の変更に失敗しました: {ex.Message}";
                    return false;
                }
            }

            target.FolderName = newFolder;
            target.Key = newKey;
            SaveSettings();
            MappingsChanged?.Invoke();
            return true;
        }

        public void RemoveFolderMapping(FolderMapping mapping)
        {
            if (_data.ImageFolderMappings.Remove(mapping))
            {
                SaveSettings();
                MappingsChanged?.Invoke();
            }
        }

        public void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_data);
                lock (_saveLock)
                {
                    string tmpPath = _settingsFilePath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _settingsFilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
