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

                    if (plane.Stride == srcStride)
                    {
                        long totalBytes = (long)srcStride * bounds.Height;
                        Buffer.MemoryCopy(srcPtr, destPtr + plane.StartIndex, capacity - (uint)plane.StartIndex, totalBytes);
                    }
                    else
                    {
                        byte* dstRow = destPtr + plane.StartIndex;
                        for (int y = 0; y < bounds.Height; y++)
                        {
                            Buffer.MemoryCopy(srcPtr + (long)y * srcStride, dstRow + (long)y * plane.Stride, srcStride, srcStride);
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

        public async Task<byte[]> CaptureRegionToJpegBytesAsync(Rectangle bounds, int targetHeight, long quality = 75L)
        {
            return await Task.Run(() => CaptureRegionToJpegBytes(bounds, targetHeight, quality));
        }

        private byte[] SaveJpeg(Bitmap bmp, long quality)
        {
            var encoder = ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (encoder == null)
            {
                using MemoryStream ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }

            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

            using MemoryStream ms2 = new MemoryStream();
            bmp.Save(ms2, encoder, encoderParameters);
            return ms2.ToArray();
        }
    }
}
