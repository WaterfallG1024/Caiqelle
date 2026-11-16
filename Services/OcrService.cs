using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Media.Ocr;

namespace Caiqelle.Services
{
    public class OcrService
    {
        public async Task<string> ExtractTextAsync(byte[] imageBytes)
        {
            var processedBytes = PreprocessImageForOcr(imageBytes);
            var softwareBitmap = await ConvertBytesToSoftwareBitmapAsync(processedBytes);
            if (softwareBitmap == null) return string.Empty;

            var ocrEngine = GetOptimalOcrEngine();
            if (ocrEngine == null) return "OCR Engine initialization failed. Ensure language packs are installed.";

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            
            var sb = new StringBuilder();
            foreach (var line in ocrResult.Lines)
            {
                string lineText = "";
                for (int i = 0; i < line.Words.Count; i++)
                {
                    string word = line.Words[i].Text;
                    
                    if (i > 0)
                    {
                        string prevWord = line.Words[i - 1].Text;
                        char lastCharOfPrev = prevWord.Length > 0 ? prevWord.Last() : ' ';
                        char firstCharOfCurrent = word.Length > 0 ? word.First() : ' ';
                        
                        bool isPrevAsian = IsAsianCharacter(lastCharOfPrev);
                        bool isCurrentAsian = IsAsianCharacter(firstCharOfCurrent);
                        
                        // 日本語などの全角文字同士の間、または全角と半角の間など、
                        // どちらかがAsian文字であればスペースを挿入しない（自然な日本語抽出のため）
                        if (!isPrevAsian && !isCurrentAsian)
                        {
                            lineText += " ";
                        }
                    }
                    lineText += word;
                }
                sb.AppendLine(lineText);
            }
            return sb.ToString().TrimEnd();
        }

        private bool IsAsianCharacter(char c)
        {
            return (c >= 0x3040 && c <= 0x309F) || // Hiragana
                   (c >= 0x30A0 && c <= 0x30FF) || // Katakana
                   (c >= 0x4E00 && c <= 0x9FBF) || // Kanji (CJK)
                   (c >= 0xFF00 && c <= 0xFFEF);   // Fullwidth roman characters
        }

        private OcrEngine GetOptimalOcrEngine()
        {
            var jpLanguage = new Windows.Globalization.Language("ja-JP");
            if (OcrEngine.IsLanguageSupported(jpLanguage))
            {
                return OcrEngine.TryCreateFromLanguage(jpLanguage);
            }
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }

        public async Task<string> ExtractTableAsTsvAsync(byte[] imageBytes)
        {
            var processedBytes = PreprocessImageForOcr(imageBytes);
            var softwareBitmap = await ConvertBytesToSoftwareBitmapAsync(processedBytes);
            if (softwareBitmap == null) return string.Empty;

            var ocrEngine = GetOptimalOcrEngine();
            if (ocrEngine == null) return "OCR Engine initialization failed.";

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            if (ocrResult.Lines.Count == 0) return string.Empty;

            // Group lines by Y coordinate to form rows
            var rowGroups = new List<List<OcrLine>>();
            var sortedLines = ocrResult.Lines.OrderBy(l => l.Words.FirstOrDefault()?.BoundingRect.Y ?? 0);

            foreach (var line in sortedLines)
            {
                if (line.Words.Count == 0) continue;
                double currentY = line.Words.First().BoundingRect.Y;
                
                bool added = false;
                foreach (var group in rowGroups)
                {
                    double groupAvgY = group.Average(l => l.Words.First().BoundingRect.Y);
                    // 15 pixels threshold for same row
                    if (Math.Abs(currentY - groupAvgY) < 15)
                    {
                        group.Add(line);
                        added = true;
                        break;
                    }
                }
                
                if (!added)
                {
                    rowGroups.Add(new List<OcrLine> { line });
                }
            }

            var sb = new StringBuilder();
            foreach (var group in rowGroups)
            {
                // Sort by X coordinate for columns
                var sortedRow = group.OrderBy(l => l.Words.First().BoundingRect.X).ToList();
                sb.AppendLine(string.Join("\t", sortedRow.Select(l => l.Text)));
            }

            return sb.ToString().TrimEnd();
        }

        private byte[] PreprocessImageForOcr(byte[] imageBytes)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(imageBytes);
                using var original = new System.Drawing.Bitmap(ms);

                // 認識精度向上のため2倍にリサイズ
                int newWidth = original.Width * 2;
                int newHeight = original.Height * 2;
                using var resized = new System.Drawing.Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                
                // グレースケール化およびコントラスト強調
                float c = 1.5f; // コントラスト倍率
                float t = (1.0f - c) / 2.0f;
                float rw = 0.3f * c;
                float gw = 0.59f * c;
                float bw = 0.11f * c;
                
                var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
                {
                    new float[] { rw, rw, rw, 0, 0 },
                    new float[] { gw, gw, gw, 0, 0 },
                    new float[] { bw, bw, bw, 0, 0 },
                    new float[] { 0,  0,  0,  1, 0 },
                    new float[] { t,  t,  t,  0, 1 }
                });
                
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetColorMatrix(cm);
                
                g.DrawImage(original, new System.Drawing.Rectangle(0, 0, newWidth, newHeight), 0, 0, original.Width, original.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                
                using var outMs = new System.IO.MemoryStream();
                resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                return outMs.ToArray();
            }
            catch
            {
                // エラー時は元の画像をそのまま返す
                return imageBytes;
            }
        }

        private async Task<SoftwareBitmap?> ConvertBytesToSoftwareBitmapAsync(byte[] imageBytes)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await stream.FlushAsync();
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                // Preprocessing already scaled the image, so we just decode it normally
                return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            catch
            {
                return null;
            }
        }
    }
}
