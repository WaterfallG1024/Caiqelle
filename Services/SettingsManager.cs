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
        public bool IsAutoCopyToClipboard { get; set; } = true;
        public bool IsIgnoreOutsideSelectionClick { get; set; } = true;
        public bool IsShiftCapsLockRewindEnabled { get; set; } = false;
        public bool IsRewindTileCompressionEnabled { get; set; } = true;
        public bool IsAutoUpdateEnabled { get; set; } = true;
        public string LastDismissedReleaseTag { get; set; } = string.Empty;
        public AccentTheme AccentTheme { get; set; } = AccentTheme.Emerald;
    }

    public class SettingsManager
    {
        private readonly string _settingsFilePath;
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

        public SettingsManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "Caiqelle");
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
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<SettingsData>(json);
                    if (loaded != null)
                    {
                        _data = loaded;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                }
            }
        }

        public event Action? MappingsChanged;

        public static string ResolveFolderPath(string folderName)
        {
            if (Path.IsPathRooted(folderName))
            {
                return folderName;
            }
            string myPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return Path.Combine(myPictures, "Caiqelle", folderName);
        }

        /// <summary>
        /// フォルダ名とキーを検証してマッピングを追加し、実フォルダを作成する
        /// </summary>
        public bool TryAddFolderMapping(string folderInput, string keyInput, bool createDirectory, out string errorMessage)
        {
            errorMessage = string.Empty;
            string folder = folderInput.Trim();
            string key = keyInput.Trim().ToUpper();

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
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
