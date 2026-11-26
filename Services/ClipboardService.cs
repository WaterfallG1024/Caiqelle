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
        private readonly SemaphoreSlim _clipboardLock = new(1, 1);

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

            var (dibBytes, pngBytes) = await Task.Run(() => PrepareClipboardPayloads(imageBytes));
            if (dibBytes == null && pngBytes == null)
                return false;

            var dispatcher = dispatcherQueue
                ?? ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.DispatcherQueue
                ?? DispatcherQueue.GetForCurrentThread();

            bool ok = false;
            // 連続コピー時の OpenClipboard / EmptyClipboard 競合を防ぐため排他制御する
            await _clipboardLock.WaitAsync();
            try
            {
                // PC起動直後の初回競合や OS クリップボード履歴サービスとの競合に備え、
                // 書き込み直後および約80ms後の再検証で消えていた場合は最大3回リトライする
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    bool written;
                    if (dispatcher != null && !dispatcher.HasThreadAccess)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        if (!dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                tcs.SetResult(WriteClipboardAndCheckImmediate(GetClipboardOwnerHwnd(), dibBytes, pngBytes));
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                            }
                        }))
                        {
                            written = WriteClipboardAndCheckImmediate(IntPtr.Zero, dibBytes, pngBytes);
                        }
                        else
                        {
                            written = await tcs.Task;
                        }
                    }
                    else
                    {
                        written = WriteClipboardAndCheckImmediate(GetClipboardOwnerHwnd(), dibBytes, pngBytes);
                    }

                    if (written)
                    {
                        await Task.Delay(80);
                        if (await VerifyClipboardFormatOnDispatcherAsync(dispatcher))
                        {
                            ok = true;
                            break;
                        }
                    }
                    await Task.Delay(50);
                }
            }
            finally
            {
                _clipboardLock.Release();
            }

            if (ok && showNotification)
            {
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

        private static IntPtr GetClipboardOwnerHwnd()
        {
            try
            {
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                var ownerWindow = app.ClipboardOwnerWindow ?? app.MainWindow;
                if (ownerWindow != null)
                {
                    return WinRT.Interop.WindowNative.GetWindowHandle(ownerWindow);
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        private static (byte[]? dibBytes, byte[]? pngBytes) PrepareClipboardPayloads(byte[] imageBytes)
        {
            try
            {
                using var ms = new MemoryStream(imageBytes);
                using var srcBmp = new Bitmap(ms);
                byte[] dibBytes = ConvertBitmapToDib(srcBmp);

                byte[] pngBytes;
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

                return (dibBytes, pngBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Payload prepare error: {ex.Message}");
                return (null, null);
            }
        }

        private static bool WriteClipboardAndCheckImmediate(IntPtr ownerHwnd, byte[]? dibBytes, byte[]? pngBytes)
        {
            if (WriteClipboardNative(ownerHwnd, dibBytes, pngBytes))
            {
                return IsClipboardFormatAvailable(CF_DIB) || (CF_PNG != 0 && IsClipboardFormatAvailable(CF_PNG));
            }
            return false;
        }

        private static async Task<bool> VerifyClipboardFormatOnDispatcherAsync(DispatcherQueue? dispatcher)
        {
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource<bool>();
                if (dispatcher.TryEnqueue(() =>
                {
                    tcs.SetResult(IsClipboardFormatAvailable(CF_DIB) || (CF_PNG != 0 && IsClipboardFormatAvailable(CF_PNG)));
                }))
                {
                    return await tcs.Task;
                }
            }
            return IsClipboardFormatAvailable(CF_DIB) || (CF_PNG != 0 && IsClipboardFormatAvailable(CF_PNG));
        }

        private static bool WriteClipboardNative(IntPtr ownerHwnd, byte[]? dibBytes, byte[]? pngBytes)
        {
            try
            {
                bool opened = false;
                for (int i = 0; i < 10; i++)
                {
                    if (OpenClipboard(ownerHwnd))
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

                    bool dibSet = dibBytes != null && SetClipboardBytes(CF_DIB, dibBytes);
                    bool pngSet = pngBytes != null && CF_PNG != 0 && SetClipboardBytes(CF_PNG, pngBytes);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

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
