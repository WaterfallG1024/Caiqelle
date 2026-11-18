using System;
using System.IO;
using Caiqelle.Services;

namespace Caiqelle.Tests
{
    public class SettingsManagerTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly SettingsManager _settings;

        public SettingsManagerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"CaiqelleTests_{Guid.NewGuid():N}");
            _settings = new SettingsManager(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }

        [Fact]
        public void DefaultSettings_HaveExpectedValues()
        {
            Assert.True(_settings.IsIgnoreOutsideSelectionClick);
            Assert.True(_settings.IsRewindTileCompressionEnabled);
        }

        [Fact]
        public void TryAddFolderMapping_ValidInput_Succeeds()
        {
            bool ok = _settings.TryAddFolderMapping("Screenshots", "s", createDirectory: false, out string err);

            Assert.True(ok);
            Assert.Equal(string.Empty, err);
            Assert.Contains(_settings.ImageFolderMappings, m => m.FolderName == "Screenshots" && m.Key == "S");
        }

        [Theory]
        [InlineData("", "A")]
        [InlineData("Test", "")]
        [InlineData("   ", "A")]
        public void TryAddFolderMapping_EmptyInput_Fails(string folder, string key)
        {
            bool ok = _settings.TryAddFolderMapping(folder, key, createDirectory: false, out string err);

            Assert.False(ok);
            Assert.NotEmpty(err);
        }

        [Theory]
        [InlineData("bad\\name")]
        [InlineData("bad/name")]
        [InlineData("bad:name")]
        [InlineData("bad*name")]
        [InlineData("bad?name")]
        public void TryAddFolderMapping_InvalidFolderChars_Fails(string folder)
        {
            bool ok = _settings.TryAddFolderMapping(folder, "A", createDirectory: false, out string err);

            Assert.False(ok);
            Assert.NotEmpty(err);
        }

        [Fact]
        public void TryAddFolderMapping_DuplicateFolderNameCaseInsensitive_Fails()
        {
            Assert.True(_settings.TryAddFolderMapping("Work", "W", createDirectory: false, out _));

            bool ok = _settings.TryAddFolderMapping("work", "X", createDirectory: false, out string err);

            Assert.False(ok);
            Assert.Contains("フォルダ名", err);
        }

        [Fact]
        public void TryAddFolderMapping_DuplicateKeyCaseInsensitive_Fails()
        {
            Assert.True(_settings.TryAddFolderMapping("Work", "W", createDirectory: false, out _));

            bool ok = _settings.TryAddFolderMapping("Other", "w", createDirectory: false, out string err);

            Assert.False(ok);
            Assert.Contains("キー", err);
        }

        [Fact]
        public void TryAddFolderMapping_DuplicateBothFolderAndKey_ReportsBoth()
        {
            Assert.True(_settings.TryAddFolderMapping("Work", "W", createDirectory: false, out _));

            bool ok = _settings.TryAddFolderMapping("work", "w", createDirectory: false, out string err);

            Assert.False(ok);
            Assert.Contains("フォルダ名", err);
            Assert.Contains("キー", err);
        }
    }
}
