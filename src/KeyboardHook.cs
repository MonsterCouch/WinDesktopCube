using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinDesktopCube;

/// <summary>
/// Low-level keyboard hook that takes over Ctrl+Win+Left/Right, the built-in
/// "switch virtual desktop" shortcuts. The keystrokes are swallowed so Windows
/// never runs its own slide animation; we raise <see cref="SwitchRequested"/> instead.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    public event Action<int>? SwitchRequested; // -1 = left, +1 = right

    private readonly Win32.LowLevelProc _proc; // keep delegate alive
    private IntPtr _hook;
    private bool _swallowLeftUp, _swallowRightUp;

    public KeyboardHook()
    {
        _proc = HookProc;
        using var mod = Process.GetCurrentProcess().MainModule!;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(mod.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool injected = (k.flags & Win32.LLKHF_INJECTED) != 0;
            int vk = (int)k.vkCode;

            if (!injected && (vk == Win32.VK_LEFT || vk == Win32.VK_RIGHT))
            {
                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
                {
                    bool win = Down(Win32.VK_LWIN) || Down(Win32.VK_RWIN);
                    if (win && Down(Win32.VK_CONTROL) && !Down(Win32.VK_SHIFT) && !Down(Win32.VK_MENU))
                    {
                        if (vk == Win32.VK_LEFT) _swallowLeftUp = true; else _swallowRightUp = true;
                        MaskWinKey();
                        SwitchRequested?.Invoke(vk == Win32.VK_LEFT ? -1 : +1);
                        return (IntPtr)1;
                    }
                }
                else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
                {
                    if (vk == Win32.VK_LEFT && _swallowLeftUp) { _swallowLeftUp = false; return (IntPtr)1; }
                    if (vk == Win32.VK_RIGHT && _swallowRightUp) { _swallowRightUp = false; return (IntPtr)1; }
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Inject an unassigned key (0xE8) so the shell sees "Win was used in a combo"
    /// and doesn't open the Start menu when Win is released.
    /// </summary>
    private static void MaskWinKey()
    {
        var inputs = new Win32.INPUT[2];
        inputs[0].type = 1; inputs[0].ki.wVk = 0xE8;
        inputs[1].type = 1; inputs[1].ki.wVk = 0xE8; inputs[1].ki.dwFlags = 2; // KEYEVENTF_KEYUP
        Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
