using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Caiqelle.Services
{
    public class ScreenshotService
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        
        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;

        public Rectangle GetVirtualScreenBounds()
        {
            return new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN)
            );
        }

        public async Task<SoftwareBitmapSource?> CaptureFullScreenAsync()
        {
            var bounds = GetVirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            // BitBlt と SoftwareBitmap への直接転送をバックグラウンドスレッドで実行し、UIスレッドのブロックと中間配列確保を排除
            var softwareBitmap = await Task.Run(() => CaptureBoundsToSoftwareBitmap(bounds));
            if (softwareBitmap == null) return null;

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
        }

        /// <summary>
        /// 指定領域をゼロエンコードで SoftwareBitmap へ直接キャプチャする（reuseBitmap 指定時は再利用してGC負荷を削減）
        /// </summary>
        public SoftwareBitmap? CaptureRegionToSoftwareBitmap(Rectangle bounds, SoftwareBitmap? reuseBitmap = null)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            return CaptureBoundsToSoftwareBitmap(bounds, reuseBitmap);
        }

        private static unsafe SoftwareBitmap? CaptureBoundsToSoftwareBitmap(Rectangle bounds, SoftwareBitmap? reuseBitmap = null)
        {
            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOldBitmap = IntPtr.Zero;

            try
            {
                hdcScreen = CreateDC("DISPLAY", null!, null!, IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) return null;

                hdcMem = CreateCompatibleDC(hdcScreen);
                if (hdcMem == IntPtr.Zero) return null;

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = bounds.Width,
                        biHeight = -bounds.Height, // トップダウン DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };

                hBitmap = CreateDIBSection(hdcScreen, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero) return null;

                hOldBitmap = SelectObject(hdcMem, hBitmap);
                BitBlt(hdcMem, 0, 0, bounds.Width, bounds.Height, hdcScreen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
                SelectObject(hdcMem, hOldBitmap);
                hOldBitmap = IntPtr.Zero;

                var softwareBitmap = (reuseBitmap != null && reuseBitmap.PixelWidth == bounds.Width && reuseBitmap.PixelHeight == bounds.Height)
                    ? reuseBitmap
                    : new SoftwareBitmap(BitmapPixelFormat.Bgra8, bounds.Width, bounds.Height, BitmapAlphaMode.Premultiplied);
                using (var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Write))
                using (var reference = buffer.CreateReference())
                {
                    var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                    byteAccess.GetBuffer(out byte* destPtr, out uint capacity);

                    var plane = buffer.GetPlaneDescription(0);
                    int srcStride = bounds.Width * 4;
                    byte* srcPtr = (byte*)pBits.ToPointer();
                    byte* dstBase = destPtr + plane.StartIndex;

                    // BitBlt 出力は Alpha=0 になる場合があるため、Premultiplied 用に全ピクセルの Alpha を 0xFF に強制する
                    for (int y = 0; y < bounds.Height; y++)
                    {
                        uint* s = (uint*)(srcPtr + (long)y * srcStride);
                        uint* d = (uint*)(dstBase + (long)y * plane.Stride);
                        for (int x = 0; x < bounds.Width; x++)
                        {
                            d[x] = s[x] | 0xFF000000u;
                        }
                    }
                }

                return softwareBitmap;
            }
            finally
            {
                if (hOldBitmap != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) DeleteDC(hdcScreen);
            }
        }

        public async Task<byte[]> CaptureRegionToBytesAsync(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return Array.Empty<byte>();

            using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            
            IntPtr hdcDest = graphics.GetHdc();
            IntPtr hdcSrc = CreateDC("DISPLAY", null!, null!, IntPtr.Zero);
            BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height, hdcSrc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            DeleteDC(hdcSrc);
            graphics.ReleaseHdc(hdcDest);

            var settings = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SettingsManager>(((App)Microsoft.UI.Xaml.Application.Current).Services);
            
            if (settings.ImageFormat == "JPEG")
            {
                return SaveJpeg(bitmap, settings.JpegQuality);
            }
            else
            {
                using MemoryStream ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        public byte[] CaptureRegionToJpegBytes(Rectangle bounds, int targetHeight, long quality = 75L)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return Array.Empty<byte>();

            using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            
            IntPtr hdcDest = graphics.GetHdc();
            IntPtr hdcSrc = CreateDC("DISPLAY", null!, null!, IntPtr.Zero);
            BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height, hdcSrc, bounds.X, bounds.Y, SRCCOPY);
            DeleteDC(hdcSrc);
            graphics.ReleaseHdc(hdcDest);

            int currentHeight = bounds.Height;
            long qualityParam = Math.Clamp(quality, 1L, 100L);

            if (targetHeight > 0 && targetHeight < currentHeight)
            {
                int targetWidth = (int)((double)bounds.Width / bounds.Height * targetHeight);
                using Bitmap resized = new Bitmap(targetWidth, targetHeight);
                using Graphics g = Graphics.FromImage(resized);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
                
                return SaveJpeg(resized, qualityParam);
            }
            else
            {
                return SaveJpeg(bitmap, qualityParam);
            }
        }

        public const int RewindTileCols = 4;
        public const int RewindTileRows = 4;
        public const int RewindTileCount = RewindTileCols * RewindTileRows;

        private static readonly ImageCodecInfo? _jpegEncoder =
            ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        /// <summary>
        /// フレーム解像度・比率に応じて16分割(4x4)のタイル矩形を動的に算出する（端数隙間なし）
        /// </summary>
        public static Rectangle GetTileRect(int tileIndex, int frameWidth, int frameHeight)
        {
            int col = tileIndex % RewindTileCols;
            int row = tileIndex / RewindTileCols;
            int x0 = (col * frameWidth) / RewindTileCols;
            int x1 = ((col + 1) * frameWidth) / RewindTileCols;
            int y0 = (row * frameHeight) / RewindTileRows;
            int y1 = ((row + 1) * frameHeight) / RewindTileRows;
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// 画面を16分割して直前フレームと比較し、変化したタイルのみ JPEG エンコードして返す
        /// </summary>
        public unsafe byte[]?[] CaptureTiledDiffJpegBytes(
            Rectangle bounds,
            int targetHeight,
            long quality,
            ref byte[]? prevRawPixels,
            ref int prevWidth,
            ref int prevHeight,
            byte[]?[]? prevTiles,
            out int outWidth,
            out int outHeight,
            out int changedTileCount)
        {
            outWidth = 0;
            outHeight = 0;
            changedTileCount = 0;

            if (bounds.Width <= 0 || bounds.Height <= 0) return Array.Empty<byte[]?>();

            using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr hdcDest = graphics.GetHdc();
                IntPtr hdcSrc = CreateDC("DISPLAY", null!, null!, IntPtr.Zero);
                BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height, hdcSrc, bounds.X, bounds.Y, SRCCOPY);
                DeleteDC(hdcSrc);
                graphics.ReleaseHdc(hdcDest);
            }

            long qualityParam = Math.Clamp(quality, 1L, 100L);
            Bitmap? resizedBmp = null;
            Bitmap workingBmp = bitmap;

            try
            {
                if (targetHeight > 0 && targetHeight < bounds.Height)
                {
                    int targetWidth = (int)((double)bounds.Width / bounds.Height * targetHeight);
                    resizedBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                    using Graphics g = Graphics.FromImage(resizedBmp);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
                    workingBmp = resizedBmp;
                }

                outWidth = workingBmp.Width;
                outHeight = workingBmp.Height;

                var rect = new Rectangle(0, 0, outWidth, outHeight);
                var bmpData = workingBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = bmpData.Stride;
                int totalBytes = stride * outHeight;

                bool canDiff = prevRawPixels != null
                    && prevTiles != null
                    && prevTiles.Length == RewindTileCount
                    && prevWidth == outWidth
                    && prevHeight == outHeight
                    && prevRawPixels.Length == totalBytes;

                var resultTiles = new byte[]?[RewindTileCount];
                var changedFlags = new bool[RewindTileCount];

                try
                {
                    byte* curBase = (byte*)bmpData.Scan0.ToPointer();

                    if (canDiff)
                    {
                        fixed (byte* prevBase = prevRawPixels)
                        {
                            for (int i = 0; i < RewindTileCount; i++)
                            {
                                var tileRect = GetTileRect(i, outWidth, outHeight);
                                int rowByteOffset = tileRect.X * 4;
                                int rowBytes = tileRect.Width * 4;
                                bool isSame = true;

                                for (int y = tileRect.Y; y < tileRect.Bottom; y++)
                                {
                                    int offset = y * stride + rowByteOffset;
                                    var curSpan = new ReadOnlySpan<byte>(curBase + offset, rowBytes);
                                    var prevSpan = new ReadOnlySpan<byte>(prevBase + offset, rowBytes);
                                    if (!curSpan.SequenceEqual(prevSpan))
                                    {
                                        isSame = false;
                                        break;
                                    }
                                }

                                if (isSame && prevTiles![i] != null)
                                {
                                    resultTiles[i] = prevTiles[i];
                                }
                                else
                                {
                                    changedFlags[i] = true;
                                }
                            }
                        }
                    }
                    else
                    {
                        Array.Fill(changedFlags, true);
                    }

                    // 次回比較用に最新ピクセルを保持（同サイズなら配列を再利用）
                    if (prevRawPixels == null || prevRawPixels.Length != totalBytes)
                    {
                        prevRawPixels = new byte[totalBytes];
                    }
                    fixed (byte* prevDst = prevRawPixels)
                    {
                        Buffer.MemoryCopy(curBase, prevDst, totalBytes, totalBytes);
                    }
                    prevWidth = outWidth;
                    prevHeight = outHeight;
                }
                finally
                {
                    workingBmp.UnlockBits(bmpData);
                }

                // 変化したタイルのみを個別に切り出して JPEG エンコード
                for (int i = 0; i < RewindTileCount; i++)
                {
                    if (!changedFlags[i]) continue;
                    var tileRect = GetTileRect(i, outWidth, outHeight);
                    if (tileRect.Width <= 0 || tileRect.Height <= 0) continue;

                    using var tileBmp = workingBmp.Clone(tileRect, PixelFormat.Format32bppArgb);
                    resultTiles[i] = SaveJpeg(tileBmp, qualityParam);
                    changedTileCount++;
                }

                return resultTiles;
            }
            finally
            {
                resizedBmp?.Dispose();
            }
        }

        /// <summary>
        /// 16分割タイル配列を1枚のJPEG画像データへ合成する
        /// </summary>
        public byte[] ComposeTilesToJpegBytes(byte[]?[] tiles, int width, int height, long quality = 90L)
        {
            if (tiles == null || tiles.Length != RewindTileCount || width <= 0 || height <= 0)
                return Array.Empty<byte>();

            using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

                for (int i = 0; i < RewindTileCount; i++)
                {
                    var tileBytes = tiles[i];
                    if (tileBytes == null || tileBytes.Length == 0) continue;

                    var tileRect = GetTileRect(i, width, height);
                    using var ms = new MemoryStream(tileBytes);
                    using var tileImg = Image.FromStream(ms);
                    g.DrawImage(tileImg, tileRect.X, tileRect.Y, tileRect.Width, tileRect.Height);
                }
            }

            return SaveJpeg(canvas, Math.Clamp(quality, 1L, 100L));
        }

        public async Task<byte[]> CaptureRegionToJpegBytesAsync(Rectangle bounds, int targetHeight, long quality = 75L)
        {
            return await Task.Run(() => CaptureRegionToJpegBytes(bounds, targetHeight, quality));
        }

        private byte[] SaveJpeg(Bitmap bmp, long quality)
        {
            if (_jpegEncoder == null)
            {
                using MemoryStream ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }

            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

            using MemoryStream ms2 = new MemoryStream();
            bmp.Save(ms2, _jpegEncoder, encoderParameters);
            return ms2.ToArray();
        }
    }
}
