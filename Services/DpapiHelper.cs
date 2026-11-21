using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Caiqelle.Services
{
    /// <summary>
    /// Windows DPAPI（CryptProtectData / CryptUnprotectData）を用いて
    /// 現在の Windows ユーザー単位で機密文字列を暗号化・復号するヘルパー
    /// </summary>
    public static class DpapiHelper
    {
        private const string LegacyPrefix = "dpapi:";
        private const string PrefixV2 = "dpapi2:";
        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        // アプリ固有エントロピー（同一ユーザー上の他プロセスからの無条件復号を難しくする）
        private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("Caiqelle.ActionVault.v2");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string? szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// 平文を DPAPI（アプリ固有エントロピー付き）で暗号化し、プレフィックス付き Base64 文字列を返す
        /// </summary>
        public static string Protect(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            IntPtr inputPtr = Marshal.AllocHGlobal(plainBytes.Length);
            IntPtr entropyPtr = Marshal.AllocHGlobal(AppEntropy.Length);
            IntPtr entropyBlobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
            try
            {
                Marshal.Copy(plainBytes, 0, inputPtr, plainBytes.Length);
                Marshal.Copy(AppEntropy, 0, entropyPtr, AppEntropy.Length);
                var entropyBlob = new DATA_BLOB { cbData = AppEntropy.Length, pbData = entropyPtr };
                Marshal.StructureToPtr(entropyBlob, entropyBlobPtr, false);

                var inBlob = new DATA_BLOB { cbData = plainBytes.Length, pbData = inputPtr };
                if (!CryptProtectData(ref inBlob, null, entropyBlobPtr, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                {
                    return string.Empty;
                }

                try
                {
                    byte[] encrypted = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
                    return PrefixV2 + Convert.ToBase64String(encrypted);
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
                Marshal.FreeHGlobal(inputPtr);
                Marshal.FreeHGlobal(entropyPtr);
                Marshal.FreeHGlobal(entropyBlobPtr);
            }
        }

        /// <summary>
        /// DPAPI 暗号化文字列を復号する（v2 エントロピー付き・旧 v1 形式の双方に対応し、未暗号化時は平文を返す）
        /// </summary>
        public static string Unprotect(string? storedText)
        {
            if (string.IsNullOrEmpty(storedText)) return string.Empty;

            bool useEntropy;
            string payload;
            if (storedText.StartsWith(PrefixV2, StringComparison.Ordinal))
            {
                useEntropy = true;
                payload = storedText[PrefixV2.Length..];
            }
            else if (storedText.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                useEntropy = false;
                payload = storedText[LegacyPrefix.Length..];
            }
            else
            {
                return storedText;
            }

            try
            {
                byte[] encrypted = Convert.FromBase64String(payload);
                IntPtr inputPtr = Marshal.AllocHGlobal(encrypted.Length);
                IntPtr entropyPtr = IntPtr.Zero;
                IntPtr entropyBlobPtr = IntPtr.Zero;
                try
                {
                    Marshal.Copy(encrypted, 0, inputPtr, encrypted.Length);
                    if (useEntropy)
                    {
                        entropyPtr = Marshal.AllocHGlobal(AppEntropy.Length);
                        entropyBlobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
                        Marshal.Copy(AppEntropy, 0, entropyPtr, AppEntropy.Length);
                        var entropyBlob = new DATA_BLOB { cbData = AppEntropy.Length, pbData = entropyPtr };
                        Marshal.StructureToPtr(entropyBlob, entropyBlobPtr, false);
                    }

                    var inBlob = new DATA_BLOB { cbData = encrypted.Length, pbData = inputPtr };
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, entropyBlobPtr, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                    {
                        return string.Empty;
                    }

                    try
                    {
                        byte[] plainBytes = new byte[outBlob.cbData];
                        Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                        string result = Encoding.UTF8.GetString(plainBytes);
                        Array.Clear(plainBytes, 0, plainBytes.Length);
                        return result;
                    }
                    finally
                    {
                        if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(inputPtr);
                    if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
                    if (entropyBlobPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlobPtr);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// ピル型 UI や一覧表示用に値をマスクする（先頭1文字＋伏せ字）
        /// </summary>
        public static string MaskValue(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            if (plainText.Length <= 2) return new string('•', plainText.Length);
            return $"{plainText[0]}{new string('•', Math.Min(6, plainText.Length - 1))}";
        }
    }
}
