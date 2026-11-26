using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Caiqelle.Services
{
    public enum CapturedKey
    {
        PrintScreen,
        ShiftPrintScreen,
        WinShiftS,
        C,
        W,
        F,
        E,
        P,
        R,
        L,
        S,
        V,
        Esc,
        OpenDashboard,
        ToggleRewind,
        ToggleAutoCopy,
        ActionApply,
        ActionDismiss
    }

    public class HookService : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private readonly System.Collections.Generic.HashSet<int> _blockedKeyUps = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int vKey);

        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_SNAPSHOT = 0x2C;
        private const int VK_S = 0x53;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_MENU = 0x12;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_CAPITAL = 0x14;

        // Scan codes for 1-key accelerators (independent of IME)
        private const uint SC_C = 0x2E;
        private const uint SC_W = 0x11;
        private const uint SC_F = 0x21;
        private const uint SC_S = 0x1F;
        private const uint SC_E = 0x12;
        private const uint SC_P = 0x19;
        private const uint SC_R = 0x13;
        private const uint SC_L = 0x26;
        private const uint SC_V = 0x2F;
        private const uint SC_ESC = 0x01;

        public event Action<CapturedKey>? OnKeyPressed;

        /// <summary>
        /// ショートカット設定ダイアログ表示中に低レベルフックから直接キーを受け取るためのコールバック
        /// </summary>
        public Action<bool, bool, bool, bool, int>? KeyCaptureCallback { get; set; }
        
        public bool IsFloatingUiActive { get; set; } = false;
        public bool IsCaptureOverlayActive { get; set; } = false;
        public bool IsActionPillActive { get; set; } = false;
        public bool IsHijackPrintScreen { get; set; } = true;
        public bool IsHijackWinShiftS { get; set; } = true;
        public bool IsRewindEnabled { get; set; } = false;
        public ShortcutBinding RewindToggleShortcut { get; set; } = ShortcutBinding.CreateDefaultRewindToggle();
        public ShortcutBinding AutoCopyToggleShortcut { get; set; } = ShortcutBinding.CreateDefaultAutoCopyToggle();

        public HookService()
        {
            _proc = HookCallback;
        }

        public void Start()
        {
            InstallHook();
        }

        private void InstallHook()
        {
            if (_hookID == IntPtr.Zero)
            {
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule!)
                {
                    _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName!), 0);
                }
            }
        }

        public void Stop()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
            _blockedKeyUps.Clear();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP))
            {
                var upStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (_blockedKeyUps.Remove((int)upStruct.vkCode))
                {
                    return (IntPtr)1;
                }
                return CallNextHookEx(_hookID, nCode, wParam, lParam);
            }

            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = (int)hookStruct.vkCode;
                uint scanCode = hookStruct.scanCode;

                // 低レベルフックではスレッド依存の GetKeyState による誤判定を避け、GetAsyncKeyState のみで物理押下状態を判定する
                bool isShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;

                bool isWin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                bool isCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                           || (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
                           || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
                bool isAlt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                          || (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                          || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

                // ショートカット変更ダイアログが開いている間は、OS（Copilot起動等）より先にここでキーを捕捉する
                var captureCb = KeyCaptureCallback;
                if (captureCb != null)
                {
                    bool isModifierOnly = vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT
                                                 or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
                                                 or VK_MENU or VK_LMENU or VK_RMENU
                                                 or VK_LWIN or VK_RWIN;
                    if (!isModifierOnly)
                    {
                        _blockedKeyUps.Add(vkCode);
                        captureCb(isCtrl, isShift, isAlt, isWin, vkCode);
                        return (IntPtr)1;
                    }
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                // 1. PrintScreen Hijack
                if (IsHijackPrintScreen && vkCode == VK_SNAPSHOT)
                {
                    if (isCtrl) OnKeyPressed?.Invoke(CapturedKey.OpenDashboard);
                    else if (isShift) OnKeyPressed?.Invoke(CapturedKey.ShiftPrintScreen);
                    else OnKeyPressed?.Invoke(CapturedKey.PrintScreen);
                    
                    return new IntPtr(1); // Block key
                }

                // 2. Win + Shift + S Hijack
                if (IsHijackWinShiftS && vkCode == VK_S && isWin && isShift)
                {
                    OnKeyPressed?.Invoke(CapturedKey.WinShiftS);
                    return (IntPtr)1;
                }

                // 3. カスタムショートカット: リワインド開始/一時停止（リワインド有効時のみ動作）
                if (IsRewindEnabled)
                {
                    if (RewindToggleShortcut.Matches(isCtrl, isShift, isAlt, isWin, vkCode))
                    {
                        _blockedKeyUps.Add(vkCode);
                        OnKeyPressed?.Invoke(CapturedKey.ToggleRewind);
                        return (IntPtr)1;
                    }
                    if (RewindToggleShortcut.VirtualKey == VK_CAPITAL && vkCode == VK_CAPITAL)
                    {
                        _blockedKeyUps.Add(vkCode);
                        return (IntPtr)1; // CapsLock 割り当て時は単体入力を無効化
                    }
                }

                // 4. カスタムショートカット: 自動クリップボードコピー切替（常に有効）
                if (AutoCopyToggleShortcut.Matches(isCtrl, isShift, isAlt, isWin, vkCode))
                {
                    _blockedKeyUps.Add(vkCode);
                    OnKeyPressed?.Invoke(CapturedKey.ToggleAutoCopy);
                    return (IntPtr)1;
                }

                // 3. 1-key accelerators (Only when Floating UI is active)
                if (IsFloatingUiActive)
                {
                    CapturedKey? capturedKey = null;
                    if (scanCode == SC_C) capturedKey = CapturedKey.C;
                    else if (scanCode == SC_W) capturedKey = CapturedKey.W;
                    else if (scanCode == SC_F) capturedKey = CapturedKey.F;
                    else if (scanCode == SC_E) capturedKey = CapturedKey.E;
                    else if (scanCode == SC_P) capturedKey = CapturedKey.P;
                    else if (scanCode == SC_R) capturedKey = CapturedKey.R;
                    else if (scanCode == SC_L) capturedKey = CapturedKey.L;
                    else if (scanCode == SC_S) capturedKey = CapturedKey.S;
                    else if (scanCode == SC_V) capturedKey = CapturedKey.V;
                    else if (scanCode == SC_ESC) capturedKey = CapturedKey.Esc;

                    if (capturedKey.HasValue)
                    {
                        OnKeyPressed?.Invoke(capturedKey.Value);
                        return (IntPtr)1; // Block default input
                    }
                }
                else if (IsCaptureOverlayActive)
                {
                    if (scanCode == SC_ESC)
                    {
                        OnKeyPressed?.Invoke(CapturedKey.Esc);
                        return (IntPtr)1;
                    }
                }
                else if (IsActionPillActive && !isCtrl && !isShift && !isWin)
                {
                    const int VK_RETURN = 0x0D;
                    const int VK_ESCAPE = 0x1B;
                    if (vkCode == VK_RETURN)
                    {
                        OnKeyPressed?.Invoke(CapturedKey.ActionApply);
                        return (IntPtr)1;
                    }
                    if (vkCode == VK_ESCAPE)
                    {
                        OnKeyPressed?.Invoke(CapturedKey.ActionDismiss);
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
