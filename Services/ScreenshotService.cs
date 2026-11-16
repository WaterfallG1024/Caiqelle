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

            using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            
            IntPtr hdcDest = graphics.GetHdc();
            IntPtr hdcSrc = CreateDC("DISPLAY", null!, null!, IntPtr.Zero);
            BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height, hdcSrc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            DeleteDC(hdcSrc);
            graphics.ReleaseHdc(hdcDest);

            // GDI+ Bitmap to SoftwareBitmap (Zero encoding overhead)
            var data = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            
            var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bounds.Width, bounds.Height, BitmapAlphaMode.Premultiplied);
            
            int bytesCount = data.Stride * bounds.Height;
            byte[] pixels = new byte[bytesCount];
            Marshal.Copy(data.Scan0, pixels, 0, bytesCount);
            
            softwareBitmap.CopyFromBuffer(pixels.AsBuffer());
            bitmap.UnlockBits(data);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(softwareBitmap);
            return source;
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
