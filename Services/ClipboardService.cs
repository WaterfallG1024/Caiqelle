using Microsoft.UI.Dispatching;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Caiqelle.Services
{
    /// <summary>
    /// Win32 ネイティブ Clipboard API を用いて画像データを確実にクリップボードへコピーするサービス
    /// （PC起動直後・バックグラウンド状態でもフォアグラウンドウィンドウ不要で初回から確実に動作）
    /// </summary>
    public class ClipboardService
    {
        private readonly NotificationService _notificationService;

        private const uint CF_DIB = 8;
        private const uint GMEM_MOVEABLE = 0x0002;
        private static readonly uint CF_PNG = RegisterClipboardFormat("PNG");

        public ClipboardService(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// 画像バイト列をクリップボードへコピーする（成功時のみ必要に応じて完了通知を表示）
        /// </summary>
        public async Task<bool> CopyImageAsync(byte[] imageBytes, bool showNotification = true, DispatcherQueue? dispatcherQueue = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return false;

            bool ok = await Task.Run(() => CopyImageNative(imageBytes));

            if (ok && showNotification)
            {
                var dispatcher = dispatcherQueue
                    ?? ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.DispatcherQueue
                    ?? DispatcherQueue.GetForCurrentThread();

                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(() =>
                        _notificationService.ShowNotification("クリップボードにコピーしました", "\uE8C8"));
                }
                else
                {
                    _notificationService.ShowNotification("クリップボードにコピーしました", "\uE8C8");
                }
            }

            return ok;
        }

        private static bool CopyImageNative(byte[] imageBytes)
        {
            try
            {
                // 画像バイト列をデコードして CF_DIB（ボトムアップ 32bpp BGRA）と PNG バイト列を生成
                byte[] dibBytes;
                byte[] pngBytes;

                using (var ms = new MemoryStream(imageBytes))
                using (var srcBmp = new Bitmap(ms))
                {
                    dibBytes = ConvertBitmapToDib(srcBmp);

                    bool isAlreadyPng = imageBytes.Length >= 8 &&
                                        imageBytes[0] == 0x89 && imageBytes[1] == 0x50 &&
                                        imageBytes[2] == 0x4E && imageBytes[3] == 0x47;
                    if (isAlreadyPng)
                    {
                        pngBytes = imageBytes;
                    }
                    else
                    {
                        using var pngMs = new MemoryStream();
                        srcBmp.Save(pngMs, ImageFormat.Png);
                        pngBytes = pngMs.ToArray();
                    }
                }

                // 他アプリのクリップボード監視と一瞬競合した場合に備えてリトライ付きで OpenClipboard
                bool opened = false;
                for (int i = 0; i < 10; i++)
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        opened = true;
                        break;
                    }
                    Thread.Sleep(25);
                }

                if (!opened)
                {
                    System.Diagnostics.Debug.WriteLine("[ClipboardService] OpenClipboard failed after retries.");
                    return false;
                }

                try
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }

                    bool dibSet = SetClipboardBytes(CF_DIB, dibBytes);
                    bool pngSet = CF_PNG != 0 && SetClipboardBytes(CF_PNG, pngBytes);

                    return dibSet || pngSet;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Copy error: {ex.Message}");
                return false;
            }
        }

        private static byte[] ConvertBitmapToDib(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;
            int rowBytes = width * 4;
            int pixelBytesSize = rowBytes * height;
            const int headerSize = 40; // BITMAPINFOHEADER

            byte[] dib = new byte[headerSize + pixelBytesSize];

            // BITMAPINFOHEADER を書き込み（正の height ＝ 標準ボトムアップ DIB）
            BitConverter.GetBytes(headerSize).CopyTo(dib, 0);        // biSize
            BitConverter.GetBytes(width).CopyTo(dib, 4);             // biWidth
            BitConverter.GetBytes(height).CopyTo(dib, 8);            // biHeight
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);         // biPlanes
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);        // biBitCount
            BitConverter.GetBytes(0).CopyTo(dib, 16);                // biCompression (BI_RGB)
            BitConverter.GetBytes(pixelBytesSize).CopyTo(dib, 20);   // biSizeImage

            using var argbBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(argbBmp))
            {
                g.DrawImage(source, 0, 0, width, height);
            }

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = argbBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // ボトムアップ順にコピー（最下行から最上行へ）
                for (int y = 0; y < height; y++)
                {
                    IntPtr srcRow = IntPtr.Add(bmpData.Scan0, (height - 1 - y) * bmpData.Stride);
                    Marshal.Copy(srcRow, dib, headerSize + y * rowBytes, rowBytes);
                }
            }
            finally
            {
                argbBmp.UnlockBits(bmpData);
            }

            return dib;
        }

        private static bool SetClipboardBytes(uint format, byte[] data)
        {
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (hGlobal == IntPtr.Zero) return false;

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
            {
                // OS へ所有権が移らなかった場合のみ解放する
                GlobalFree(hGlobal);
                return false;
            }

            return true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
