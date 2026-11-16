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
        public bool IsShiftCapsLockRewindEnabled { get; set; } = false;
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

        public bool IsShiftCapsLockRewindEnabled
        {
            get => _data.IsShiftCapsLockRewindEnabled;
            set => _data.IsShiftCapsLockRewindEnabled = value;
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
