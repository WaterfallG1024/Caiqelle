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
    public record OcrLineBox(string Text, System.Drawing.Rectangle Bounds);

    public class OcrService
    {
        public async Task<string> ExtractTextAsync(byte[] imageBytes)
        {
            var processedBytes = PreprocessImageForOcr(imageBytes);
            using var softwareBitmap = await ConvertBytesToSoftwareBitmapAsync(processedBytes);
            if (softwareBitmap == null) return string.Empty;

            var ocrEngine = GetOptimalOcrEngine();
            if (ocrEngine == null) return "OCR Engine initialization failed. Ensure language packs are installed.";

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            
            var sb = new StringBuilder();
            foreach (var line in ocrResult.Lines)
            {
                sb.AppendLine(BuildLineText(line));
            }
            return sb.ToString().TrimEnd();
        }

        private OcrEngine? _cachedOcrEngine;
        private SoftwareBitmap? _scaledOcrBitmap;
        private readonly object _scaleLock = new();
        private static readonly byte[] ThinTextGammaLut = BuildThinTextGammaLut();

        private static byte[] BuildThinTextGammaLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                if (i >= 232)
                {
                    lut[i] = (byte)i;
                }
                else
                {
                    int v = (int)Math.Round(232.0 * Math.Pow(i / 232.0, 1.35));
                    lut[i] = (byte)Math.Clamp(v, 0, 232);
                }
            }
            return lut;
        }

        /// <summary>
        /// SoftwareBitmap を並列処理で高速拡大＆薄文字強調し、OCR 解析して元の座標系の行と矩形を返す
        /// </summary>
        public async Task<IReadOnlyList<OcrLineBox>> ExtractLinesWithBoundsAsync(SoftwareBitmap softwareBitmap)
        {
            if (softwareBitmap == null)
                return Array.Empty<OcrLineBox>();

            var ocrEngine = _cachedOcrEngine ??= GetOptimalOcrEngine();
            if (ocrEngine == null)
                return Array.Empty<OcrLineBox>();

            SoftwareBitmap targetBitmap = PrepareScaledSoftwareBitmap(softwareBitmap, 1.25f, out float actualScale);
            var ocrResult = await ocrEngine.RecognizeAsync(targetBitmap);
            return ConvertOcrResultToLines(ocrResult, actualScale);
        }

        private unsafe SoftwareBitmap PrepareScaledSoftwareBitmap(SoftwareBitmap srcBitmap, float targetScale, out float actualScale)
        {
            actualScale = 1.0f;
            try
            {
                int srcW = srcBitmap.PixelWidth;
                int srcH = srcBitmap.PixelHeight;
                if (srcW <= 0 || srcH <= 0) return srcBitmap;

                float scale = targetScale;
                if (srcW * scale > 2560f)
                {
                    scale = Math.Max(1.0f, 2560f / srcW);
                }

                int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                actualScale = (float)dstW / srcW;

                lock (_scaleLock)
                {
                    if (_scaledOcrBitmap == null || _scaledOcrBitmap.PixelWidth != dstW || _scaledOcrBitmap.PixelHeight != dstH)
                    {
                        _scaledOcrBitmap?.Dispose();
                        _scaledOcrBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, dstW, dstH, BitmapAlphaMode.Premultiplied);
                    }

                    using var srcBuffer = srcBitmap.LockBuffer(BitmapBufferAccessMode.Read);
                    using var srcRef = srcBuffer.CreateReference();
                    using var dstBuffer = _scaledOcrBitmap.LockBuffer(BitmapBufferAccessMode.Write);
                    using var dstRef = dstBuffer.CreateReference();

                    var srcAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(srcRef);
                    var dstAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(dstRef);
                    if (srcAccess == null || dstAccess == null)
                    {
                        actualScale = 1.0f;
                        return srcBitmap;
                    }

                    srcAccess.GetBuffer(out byte* srcData, out _);
                    dstAccess.GetBuffer(out byte* dstData, out _);

                    var srcPlane = srcBuffer.GetPlaneDescription(0);
                    var dstPlane = dstBuffer.GetPlaneDescription(0);
                    IntPtr srcBasePtr = (IntPtr)(srcData + srcPlane.StartIndex);
                    IntPtr dstBasePtr = (IntPtr)(dstData + dstPlane.StartIndex);
                    int srcStride = srcPlane.Stride;
                    int dstStride = dstPlane.Stride;
                    int maxSrcX = srcW - 1;
                    int maxSrcY = srcH - 1;
                    byte[] lut = ThinTextGammaLut;

                    // 行単位の並列バイリニア補間＋薄文字強調LUTで前処理時間を大幅短縮（並列数を制限して CPU スパイクを抑制）
                    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) };
                    Parallel.For(0, dstH, parallelOptions, y =>
                    {
                        byte* sBase = (byte*)srcBasePtr;
                        byte* dBase = (byte*)dstBasePtr;

                        float fy = (y + 0.5f) * srcH / dstH - 0.5f;
                        int y0 = (int)MathF.Floor(fy);
                        int wy = (int)((fy - y0) * 256.0f);
                        if (y0 < 0) { y0 = 0; wy = 0; }
                        int y1 = y0 < maxSrcY ? y0 + 1 : maxSrcY;
                        int invWy = 256 - wy;

                        byte* row0 = sBase + (long)y0 * srcStride;
                        byte* row1 = sBase + (long)y1 * srcStride;
                        byte* dstRow = dBase + (long)y * dstStride;

                        for (int x = 0; x < dstW; x++)
                        {
                            float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                            int x0 = (int)MathF.Floor(fx);
                            int wx = (int)((fx - x0) * 256.0f);
                            if (x0 < 0) { x0 = 0; wx = 0; }
                            int x1 = x0 < maxSrcX ? x0 + 1 : maxSrcX;
                            int invWx = 256 - wx;

                            int off0 = x0 * 4;
                            int off1 = x1 * 4;
                            int w00 = invWx * invWy;
                            int w10 = wx * invWy;
                            int w01 = invWx * wy;
                            int w11 = wx * wy;

                            int b = (row0[off0] * w00 + row0[off1] * w10 + row1[off0] * w01 + row1[off1] * w11) >> 16;
                            int g = (row0[off0 + 1] * w00 + row0[off1 + 1] * w10 + row1[off0 + 1] * w01 + row1[off1 + 1] * w11) >> 16;
                            int r = (row0[off0 + 2] * w00 + row0[off1 + 2] * w10 + row1[off0 + 2] * w01 + row1[off1 + 2] * w11) >> 16;

                            int dstOff = x * 4;
                            dstRow[dstOff] = lut[b & 0xFF];
                            dstRow[dstOff + 1] = lut[g & 0xFF];
                            dstRow[dstOff + 2] = lut[r & 0xFF];
                            dstRow[dstOff + 3] = 0xFF;
                        }
                    });

                    return _scaledOcrBitmap;
                }
            }
            catch
            {
                actualScale = 1.0f;
                return srcBitmap;
            }
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        /// <summary>
        /// 画像を拡大・コントラスト補正して OCR 解析し、元の画像座標系へ戻した行と座標矩形を返す
        /// </summary>
        public async Task<IReadOnlyList<OcrLineBox>> ExtractLinesWithBoundsAsync(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return Array.Empty<OcrLineBox>();

            const float ocrScale = 1.5f;
            byte[] preparedBytes = PreprocessImageForOcrWithScale(imageBytes, ocrScale, out float actualScale);

            using var softwareBitmap = await ConvertBytesToSoftwareBitmapAsync(preparedBytes);
            if (softwareBitmap == null)
                return Array.Empty<OcrLineBox>();

            var ocrEngine = _cachedOcrEngine ??= GetOptimalOcrEngine();
            if (ocrEngine == null)
                return Array.Empty<OcrLineBox>();

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            return ConvertOcrResultToLines(ocrResult, actualScale);
        }

        private IReadOnlyList<OcrLineBox> ConvertOcrResultToLines(OcrResult ocrResult, float actualScale)
        {
            var list = new List<OcrLineBox>(ocrResult.Lines.Count);

            foreach (var line in ocrResult.Lines)
            {
                if (line.Words.Count == 0) continue;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxR = double.MinValue, maxB = double.MinValue;
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.X + r.Width > maxR) maxR = r.X + r.Width;
                    if (r.Y + r.Height > maxB) maxB = r.Y + r.Height;
                }

                var rect = new System.Drawing.Rectangle(
                    (int)Math.Floor(minX / actualScale),
                    (int)Math.Floor(minY / actualScale),
                    Math.Max(1, (int)Math.Ceiling((maxR - minX) / actualScale)),
                    Math.Max(1, (int)Math.Ceiling((maxB - minY) / actualScale)));

                list.Add(new OcrLineBox(BuildLineText(line), rect));
            }

            return list;
        }

        private byte[] PreprocessImageForOcrWithScale(byte[] imageBytes, float targetScale, out float actualScale)
        {
            actualScale = 1.0f;
            try
            {
                using var ms = new System.IO.MemoryStream(imageBytes);
                using var original = new System.Drawing.Bitmap(ms);

                // メモリと速度のバランスを取りつつ小さい文字の認識率を上げるため最大 3600px 幅までに抑えて拡大
                float scale = targetScale;
                if (original.Width * scale > 3600)
                {
                    scale = Math.Max(1.0f, 3600f / original.Width);
                }

                int newWidth = Math.Max(1, (int)Math.Round(original.Width * scale));
                int newHeight = Math.Max(1, (int)Math.Round(original.Height * scale));
                actualScale = (float)newWidth / original.Width;

                using var resized = new System.Drawing.Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using var g = System.Drawing.Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                // 薄いグレーのプレースホルダー文字（「回答を入力」等）の認識率を上げるためコントラストを強調
                float c = 1.45f;
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
                actualScale = 1.0f;
                return imageBytes;
            }
        }

        private string BuildLineText(OcrLine line)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < line.Words.Count; i++)
            {
                string word = line.Words[i].Text;
                if (i > 0)
                {
                    string prevWord = line.Words[i - 1].Text;
                    char lastCharOfPrev = prevWord.Length > 0 ? prevWord[^1] : ' ';
                    char firstCharOfCurrent = word.Length > 0 ? word[0] : ' ';

                    // 日本語などの全角文字同士・全角と半角の間はスペースを挿入しない
                    if (!IsAsianCharacter(lastCharOfPrev) && !IsAsianCharacter(firstCharOfCurrent))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(word);
            }
            return sb.ToString();
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
