using Caiqelle.Services;
using System.Collections.Generic;
using System.Drawing;
using Xunit;

namespace Caiqelle.Tests
{
    public class ActionServiceTests
    {
        [Fact]
        public void DpapiHelper_ProtectAndUnprotect_RoundTripsCorrectly()
        {
            const string secret = "yamada.taro@example.com";
            string encrypted = DpapiHelper.Protect(secret);

            Assert.StartsWith("dpapi2:", encrypted);
            Assert.NotEqual(secret, encrypted);
            Assert.Equal(secret, DpapiHelper.Unprotect(encrypted));
        }

        [Fact]
        public void DpapiHelper_MaskValue_HidesMiddleCharacters()
        {
            Assert.Equal("y••••••", DpapiHelper.MaskValue("yamada@example.com"));
            Assert.Equal("••", DpapiHelper.MaskValue("ab"));
        }

        [Fact]
        public void DetectFormFields_IgnoresPlainArticleWithoutForm()
        {
            var lines = new List<OcrLineBox>
            {
                new("名前について", new Rectangle(40, 40, 120, 24)),
                new("名前", new Rectangle(40, 90, 60, 22)),
                new("この記事では命名規則を解説します。", new Rectangle(40, 130, 320, 22))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "命名規則の解説ブログ - Google Chrome",
                Point.Empty,
                mappings);

            Assert.Empty(result);
        }

        [Fact]
        public void DetectFormFields_DetectsUnfilledGoogleFormField()
        {
            var lines = new List<OcrLineBox>
            {
                new("参加申込フォーム", new Rectangle(100, 40, 200, 30)),
                new("名前 *", new Rectangle(100, 120, 70, 22)),
                new("回答を入力", new Rectangle(100, 160, 90, 20)),
                new("送信", new Rectangle(100, 300, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前,氏名", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "参加申込 - Google フォーム - Google Chrome",
                new Point(200, 100),
                mappings);

            Assert.Single(result);
            Assert.Equal("名前", result[0].Label);
            Assert.Equal("山田 太郎", result[0].PlainValue);
            Assert.Equal(332, result[0].TargetScreenPoint.X);
            Assert.Equal(270, result[0].TargetScreenPoint.Y);
        }

        [Fact]
        public void DetectFormFields_SkipsAlreadyFilledField()
        {
            var lines = new List<OcrLineBox>
            {
                new("名前 *", new Rectangle(100, 120, 70, 22)),
                new("山田 太郎", new Rectangle(100, 160, 100, 22)),
                new("送信", new Rectangle(100, 300, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "参加申込 - Google フォーム - Google Chrome",
                Point.Empty,
                mappings);

            Assert.Empty(result);
        }

        [Fact]
        public void DetectFormFields_MatchesPartialLabelSuchAsOnamae()
        {
            var lines = new List<OcrLineBox>
            {
                new("お名前（フルネーム） *", new Rectangle(100, 120, 180, 22)),
                new("回答を入力", new Rectangle(100, 165, 90, 20)),
                new("送信", new Rectangle(100, 300, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "アンケート - Google フォーム - Google Chrome",
                Point.Empty,
                mappings);

            Assert.Single(result);
            Assert.Equal("名前", result[0].Label);
        }

        [Fact]
        public void DetectFormFields_DetectsMultipleFieldsInSingleForm()
        {
            var lines = new List<OcrLineBox>
            {
                new("名前 *", new Rectangle(100, 100, 70, 22)),
                new("回答を入力", new Rectangle(100, 145, 90, 20)),
                new("出席番号 *", new Rectangle(100, 210, 95, 22)),
                new("回答を入力", new Rectangle(100, 255, 90, 20)),
                new("送信", new Rectangle(100, 340, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前", PlainValue = "山田 太郎" },
                new() { Keywords = "出席番号", PlainValue = "15" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "出席確認 - Google フォーム - Google Chrome",
                Point.Empty,
                mappings);

            Assert.Equal(2, result.Count);
            Assert.Equal("名前", result[0].Label);
            Assert.Equal("出席番号", result[1].Label);
            Assert.True(result[0].TargetScreenPoint.Y < result[1].TargetScreenPoint.Y);
            Assert.NotEqual(result[0].TargetScreenPoint, result[1].TargetScreenPoint);
        }

        [Fact]
        public void DetectFormFields_PrefersQuestionWithPlaceholderOverTitleContainingKeyword()
        {
            var lines = new List<OcrLineBox>
            {
                new("名前登録テスト", new Rectangle(100, 30, 180, 32)),
                new("説明テキストです", new Rectangle(100, 75, 160, 20)),
                new("お名前 *", new Rectangle(100, 140, 80, 22)),
                new("回答を入力", new Rectangle(100, 185, 90, 20)),
                new("送信", new Rectangle(100, 300, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Keywords = "名前,氏名", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(
                lines,
                "名前登録テスト - Google フォーム - Google Chrome",
                Point.Empty,
                mappings);

            Assert.Single(result);
            // タイトル (Y=30) ではなく、質問ラベル下の「回答を入力」(Y=185..205) が選ばれること
            Assert.InRange(result[0].TargetScreenPoint.Y, 185, 205);
        }

        [Fact]
        public void DetectFormFields_IsIndependentOfRegistrationOrderAndFormOrder_EvenWhenOnePlaceholderIsMissing()
        {
            // シナリオ1: フォーム上では1番目が「名前 *」（フォーカス中でプレースホルダー非表示）、2番目が「出席番号 *」（プレースホルダーあり）
            var linesNameFirst = new List<OcrLineBox>
            {
                new("名前 *", new Rectangle(100, 100, 70, 22)),
                // 1番目の「回答を入力」はフォーカス中またはOCR欠落で存在しない想定
                new("出席番号 *", new Rectangle(100, 210, 95, 22)),
                new("回答を入力", new Rectangle(100, 255, 90, 20)),
                new("送信", new Rectangle(100, 350, 50, 26))
            };

            // シナリオ2: フォーム上では1番目が「出席番号 *」、2番目が「氏名 *」
            var linesNumberFirst = new List<OcrLineBox>
            {
                new("出席番号 *", new Rectangle(100, 100, 95, 22)),
                new("回答を入力", new Rectangle(100, 145, 90, 20)),
                new("氏名 *", new Rectangle(100, 210, 70, 22)),
                new("回答を入力", new Rectangle(100, 255, 90, 20)),
                new("送信", new Rectangle(100, 350, 50, 26))
            };

            var mappingsOrderA = new List<ActionFieldMapping>
            {
                new() { Id = "name", Keywords = "名前,氏名", PlainValue = "山田 太郎" },
                new() { Id = "num", Keywords = "出席番号", PlainValue = "15" }
            };

            var mappingsOrderB = new List<ActionFieldMapping>
            {
                new() { Id = "num", Keywords = "出席番号", PlainValue = "15" },
                new() { Id = "name", Keywords = "氏名,名前", PlainValue = "山田 太郎" }
            };

            // 登録順 A / B いずれでも、1番目の「名前 *」が2番目の「出席番号 *」のプレースホルダーを奪わず別々の座標になること
            var res1A = ActionService.DetectFormFields(linesNameFirst, "出席確認 - Google フォーム", Point.Empty, mappingsOrderA);
            var res1B = ActionService.DetectFormFields(linesNameFirst, "出席確認 - Google フォーム", Point.Empty, mappingsOrderB);

            Assert.Equal(2, res1A.Count);
            Assert.Equal(2, res1B.Count);
            Assert.InRange(res1A[0].TargetScreenPoint.Y, 150, 185); // 名前欄の座標
            Assert.InRange(res1A[1].TargetScreenPoint.Y, 255, 275); // 出席番号欄の座標
            Assert.Equal(res1A[0].TargetScreenPoint, res1B[0].TargetScreenPoint);
            Assert.Equal(res1A[1].TargetScreenPoint, res1B[1].TargetScreenPoint);

            // フォーム順が逆（出席番号 -> 氏名）でも、登録順 A / B にかかわらず両方正確に検出されること
            var res2A = ActionService.DetectFormFields(linesNumberFirst, "出席確認 - Google フォーム", Point.Empty, mappingsOrderA);
            var res2B = ActionService.DetectFormFields(linesNumberFirst, "出席確認 - Google フォーム", Point.Empty, mappingsOrderB);

            Assert.Equal(2, res2A.Count);
            Assert.Equal(2, res2B.Count);
            Assert.Equal("15", res2A[0].PlainValue);
            Assert.Equal("山田 太郎", res2A[1].PlainValue);
            Assert.Equal(res2A[0].TargetScreenPoint, res2B[0].TargetScreenPoint);
            Assert.Equal(res2A[1].TargetScreenPoint, res2B[1].TargetScreenPoint);
        }

        [Fact]
        public void DetectFormFields_DetectsLabelContainingNyuryokuShiteKudasai()
        {
            var lines = new List<OcrLineBox>
            {
                new("名前を入力してください *", new Rectangle(100, 100, 200, 22)),
                new("回答を入力", new Rectangle(100, 145, 90, 20)),
                new("出席番号 *", new Rectangle(100, 220, 95, 22)),
                new("回答を入力", new Rectangle(100, 265, 90, 20)),
                new("送信", new Rectangle(100, 350, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "name", Keywords = "名前,氏名", PlainValue = "山田 太郎" },
                new() { Id = "num", Keywords = "出席番号", PlainValue = "15" }
            };

            var result = ActionService.DetectFormFields(lines, "テスト - Google Chrome", Point.Empty, mappings);
            Assert.Equal(2, result.Count);
            Assert.Equal("山田 太郎", result[0].PlainValue);
            Assert.Equal("15", result[1].PlainValue);
        }

        [Fact]
        public async System.Threading.Tasks.Task OcrAndDetect_RecognizesShortKanjiNameAndShimeiOnRenderedForm()
        {
            using var bmp = new Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(240, 235, 248));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var cardBrush = new SolidBrush(Color.White);
                using var titleFont = new Font("Yu Gothic UI", 12f, FontStyle.Regular);
                using var phFont = new Font("Yu Gothic UI", 10.5f, FontStyle.Regular);
                using var darkBrush = new SolidBrush(Color.FromArgb(32, 33, 36));
                using var phBrush = new SolidBrush(Color.FromArgb(112, 117, 122));
                using var redBrush = new SolidBrush(Color.FromArgb(217, 48, 37));

                // カード1: 氏名 *
                g.FillRectangle(cardBrush, 100, 100, 600, 130);
                g.DrawString("氏名", titleFont, darkBrush, 124, 124);
                g.DrawString("*", titleFont, redBrush, 168, 124);
                g.DrawString("回答を入力", phFont, phBrush, 124, 174);

                // カード2: 出席番号 *
                g.FillRectangle(cardBrush, 100, 250, 600, 130);
                g.DrawString("出席番号", titleFont, darkBrush, 124, 274);
                g.DrawString("*", titleFont, redBrush, 200, 274);
                g.DrawString("回答を入力", phFont, phBrush, 124, 324);

                g.DrawString("送信", titleFont, darkBrush, 100, 420);
            }

            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            var ocrService = new OcrService();

            // SoftwareBitmap 経路を直接検証
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await ras.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(ms.ToArray()));
            ras.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            using var sb = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

            var lines = await ocrService.ExtractLinesWithBoundsAsync(sb);
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "name", Keywords = "名前,氏名", PlainValue = "山田 太郎" },
                new() { Id = "num", Keywords = "出席番号", PlainValue = "15" }
            };

            var detected = ActionService.DetectFormFields(lines, "テスト - Google Chrome", Point.Empty, mappings);
            Assert.Equal(2, detected.Count);
        }

        [Fact]
        public void DetectFormFields_SkipsAlreadyFilledFieldEvenWhenDescriptionLineContainsKeyword()
        {
            // 質問ラベルのすぐ下に同じキーワードを含む説明文がある場合に、
            // 説明文が質問境界として入力値("20")の検出を分断しないことを検証
            var lines = new List<OcrLineBox>
            {
                new("出席番号を入力してください。", new Rectangle(666, 602, 212, 16)),
                new("7桁の出席番号を入力します。", new Rectangle(665, 629, 238, 14)),
                new("20", new Rectangle(666, 683, 16, 12)),
                new("氏名を入力してください。", new Rectangle(666, 776, 181, 16)),
                new("テストさん", new Rectangle(666, 833, 70, 13)),
                new("送信", new Rectangle(666, 950, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "name", Keywords = "名前,氏名", PlainValue = "テストさん" },
                new() { Id = "num", Keywords = "出席番号", PlainValue = "20" }
            };

            var result = ActionService.DetectFormFields(lines, "テストフォーム - Google Chrome", Point.Empty, mappings);
            Assert.Empty(result);
        }

        [Fact]
        public void DetectFormFields_SkipsAlreadyFilledFieldEvenWithCaretNoiseInOcr()
        {
            var lines = new List<OcrLineBox>
            {
                new("氏名を入力してください。", new Rectangle(666, 776, 181, 16)),
                new("テス|トさん", new Rectangle(666, 833, 72, 13)),
                new("送信", new Rectangle(666, 950, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "name", Keywords = "名前,氏名", PlainValue = "テストさん" }
            };

            var result = ActionService.DetectFormFields(lines, "テストフォーム - Google Chrome", Point.Empty, mappings);
            Assert.Empty(result);
        }

        [Fact]
        public void StartupService_BuildScheduledTaskXml_SetsBatteryAndUnlimitedTimeOptions()
        {
            string xml = StartupService.BuildScheduledTaskXml(@"C:\Program Files\Caiqelle\Caiqelle.exe", @"DOMAIN\User&Test");

            Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
            Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
            Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
            Assert.Contains("<Arguments>--background</Arguments>", xml);
            Assert.Contains(@"<Command>C:\Program Files\Caiqelle\Caiqelle.exe</Command>", xml);
            Assert.Contains("<UserId>DOMAIN\\User&amp;Test</UserId>", xml);
        }

        [Fact]
        public void DetectFormFields_SplitsKeywordsOnlyByHalfOrFullWidthComma()
        {
            var lines = new List<OcrLineBox>
            {
                new("氏名 *", new Rectangle(100, 100, 70, 22)),
                new("回答を入力", new Rectangle(100, 145, 90, 20)),
                new("送信", new Rectangle(100, 240, 50, 26))
            };
            // 全角カンマ「，」では分割され、スラッシュ「/」では分割されないこと
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "k1", Keywords = "名前/名称", PlainValue = "無効" },
                new() { Id = "k2", Keywords = "名前，氏名", PlainValue = "山田 太郎" }
            };

            var result = ActionService.DetectFormFields(lines, "テストフォーム - Google Chrome", Point.Empty, mappings);
            Assert.Single(result);
            Assert.Equal("山田 太郎", result[0].PlainValue);

            // スラッシュで区切っても分割されないため「氏名」には一致しないこと
            var slashOnlyMappings = new List<ActionFieldMapping>
            {
                new() { Id = "k3", Keywords = "名前/氏名", PlainValue = "山田 太郎" }
            };
            var slashResult = ActionService.DetectFormFields(lines, "テストフォーム - Google Chrome", Point.Empty, slashOnlyMappings);
            Assert.Empty(slashResult);
        }

        [Fact]
        public void DetectFormFields_IgnoresNegativeInstructionSentence()
        {
            var lines = new List<OcrLineBox>
            {
                new("最大250文字までです。氏名やメールアドレスなどの個人情報は入力しないでください。", new Rectangle(100, 100, 500, 22)),
                new("送信", new Rectangle(100, 200, 50, 26))
            };
            var mappings = new List<ActionFieldMapping>
            {
                new() { Id = "n1", Keywords = "氏名", PlainValue = "山田 太郎" },
                new() { Id = "e1", Keywords = "メールアドレス", PlainValue = "taro@example.com" }
            };

            var result = ActionService.DetectFormFields(lines, "Block user - Google Chrome", Point.Empty, mappings);
            Assert.Empty(result);
        }

        [Fact]
        public void IsUrlAllowedByWhitelist_MatchesConfiguredDomains()
        {
            var whitelist = new List<string> { "docs.google.com/forms", "example.org" };

            Assert.True(ActionService.IsUrlAllowedByWhitelist("https://docs.google.com/forms/d/e/123/viewform", whitelist));
            Assert.True(ActionService.IsUrlAllowedByWhitelist("docs.google.com/forms/d/e/123/viewform", new[] { "https://docs.google.com/forms" }));
            Assert.True(ActionService.IsUrlAllowedByWhitelist("https://sub.example.org/entry", whitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://docs.google.com/spreadsheets/d/123", whitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://evil.com/?redirect=docs.google.com/forms", whitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://example.org.evil.com/login", whitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://github.com/settings/blocked_users", whitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist(null, whitelist));
        }

        [Fact]
        public void IsUrlAllowedByWhitelist_BlocksBypassAttempts()
        {
            var httpsOnlyWhitelist = new List<string> { "https://docs.google.com/forms", "https://example.org/app" };

            // Userinfo 偽装
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://docs.google.com:pass@evil.com/forms", httpsOnlyWhitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://docs.google.com@evil.com/forms", httpsOnlyWhitelist));
            // バックスラッシュやコロンによるローカルパス誤判定バイパス
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://evil.com/\\docs.google.com/forms", httpsOnlyWhitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("http://a:b@evil.com/docs.google.com/forms", httpsOnlyWhitelist));
            // スキームダウングレード・危険スキーム
            Assert.False(ActionService.IsUrlAllowedByWhitelist("http://docs.google.com/forms/d/1", httpsOnlyWhitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("javascript:alert('docs.google.com')", httpsOnlyWhitelist));
            Assert.False(ActionService.IsUrlAllowedByWhitelist("data:text/html,docs.google.com", httpsOnlyWhitelist));
            // パス境界バイパス
            Assert.False(ActionService.IsUrlAllowedByWhitelist("https://example.org/app-evil", httpsOnlyWhitelist));
            Assert.True(ActionService.IsUrlAllowedByWhitelist("https://example.org/app/sub", httpsOnlyWhitelist));
            // file:// でドメイン名を含むローカル偽装パスを拒否
            Assert.False(ActionService.IsUrlAllowedByWhitelist("file:///C:/evil/docs.google.com/fake.html", httpsOnlyWhitelist));
        }

        [Fact]
        public void ShortcutBinding_MatchesAndFormatsCorrectly()
        {
            var binding = new ShortcutBinding { Ctrl = true, Shift = true, VirtualKey = 0x43 };
            Assert.Equal(3, binding.KeyCount);
            Assert.True(binding.IsValid);
            Assert.Equal("Ctrl + Shift + C", binding.DisplayText);
            Assert.True(binding.Matches(ctrl: true, shift: true, alt: false, win: false, vk: 0x43));
            Assert.False(binding.Matches(ctrl: true, shift: false, alt: false, win: false, vk: 0x43));
        }
    }
}
