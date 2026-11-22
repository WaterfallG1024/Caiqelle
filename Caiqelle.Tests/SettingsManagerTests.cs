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
            Assert.False(_settings.IsRewindTileCompressionEnabled);
            Assert.False(_settings.IsAutoCopyToClipboard);
            Assert.True(_settings.IsStickyTextStealthMode);
            Assert.True(_settings.IsStickyTextSaveEnabled);
            Assert.Equal(AccentTheme.System, _settings.AccentTheme);
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

        [Fact]
        public void TryUpdateFolderMapping_RenamesExistingFolderAndUpdatesKey()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CaiqelleTests_" + Guid.NewGuid().ToString("N"));
            string uniqueOld = "OldFolder_" + Guid.NewGuid().ToString("N")[..8];
            string uniqueNew = "NewFolder_" + Guid.NewGuid().ToString("N")[..8];
            string oldRealPath = SettingsManager.ResolveFolderPath(uniqueOld);
            string newRealPath = SettingsManager.ResolveFolderPath(uniqueNew);

            try
            {
                var mgr = new SettingsManager(tempDir);
                Assert.True(mgr.TryAddFolderMapping(uniqueOld, "Z", createDirectory: true, out _));
                Assert.True(Directory.Exists(oldRealPath));

                var target = Assert.Single(mgr.ImageFolderMappings, m => m.FolderName == uniqueOld);
                bool updated = mgr.TryUpdateFolderMapping(target, uniqueNew, "Y", out string err);

                Assert.True(updated, err);
                Assert.Equal(uniqueNew, target.FolderName);
                Assert.Equal("Y", target.Key);
                Assert.False(Directory.Exists(oldRealPath));
                Assert.True(Directory.Exists(newRealPath));
            }
            finally
            {
                try { if (Directory.Exists(oldRealPath)) Directory.Delete(oldRealPath, true); } catch { }
                try { if (Directory.Exists(newRealPath)) Directory.Delete(newRealPath, true); } catch { }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TryAddFolderMapping_Rejects45thMappingWhenAtMaxCount()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CaiqelleTests_" + Guid.NewGuid().ToString("N"));
            try
            {
                var mgr = new SettingsManager(tempDir);
                const string allKeys = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/[]-^;:";
                int idx = 0;
                while (mgr.ImageFolderMappings.Count < SettingsManager.MaxFolderMappingCount && idx < allKeys.Length)
                {
                    string key = allKeys[idx++].ToString();
                    mgr.TryAddFolderMapping($"Folder_{idx}", key, createDirectory: false, out _);
                }

                Assert.Equal(SettingsManager.MaxFolderMappingCount, mgr.ImageFolderMappings.Count);
                bool added = mgr.TryAddFolderMapping("Folder_45", "@", createDirectory: false, out string err);

                Assert.False(added);
                Assert.Contains("44", err);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
