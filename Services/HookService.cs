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
        ToggleRewind
    }

    public class HookService : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

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
        
        public bool IsFloatingUiActive { get; set; } = false;
        public bool IsCaptureOverlayActive { get; set; } = false;
        public bool IsHijackPrintScreen { get; set; } = true;
        public bool IsHijackWinShiftS { get; set; } = true;
        public bool IsShiftCapsLockRewindEnabled { get; set; } = false;

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
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = (int)hookStruct.vkCode;
                uint scanCode = hookStruct.scanCode;

                bool isShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0
                            || (GetKeyState(VK_SHIFT) & 0x8000) != 0;

                bool isWin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                bool isCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetKeyState(VK_CONTROL) & 0x8000) != 0;

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

                // 3. Shift + CapsLock Rewind Toggle & Block CapsLock
                if (IsShiftCapsLockRewindEnabled && vkCode == VK_CAPITAL)
                {
                    if (isShift)
                    {
                        OnKeyPressed?.Invoke(CapturedKey.ToggleRewind);
                    }
                    return (IntPtr)1; // Block CapsLock functionality completely when enabled
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
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
