using System.Runtime.InteropServices;
using VirtualDesktop;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

uint hookThreadId = 0;
IntPtr hookHandle = IntPtr.Zero;
var ready = new ManualResetEventSlim();

// Delegate must stay referenced to prevent GC while hook is active
NativeMethods.HookProc callback = (nCode, wParam, lParam) =>
{
    if (nCode >= 0 && wParam == NativeMethods.WM_KEYDOWN)
    {
        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        Console.WriteLine($"[hook] vkCode=0x{info.vkCode:X2}");
        try
        {
            int current = Desktop.FromDesktop(Desktop.Current);
            if (info.vkCode == NativeMethods.VK_F11 && current > 0)
            {
                Desktop.FromIndex(current - 1).MakeVisible();
                Console.WriteLine($"  -> switched to {current - 1}");
                return 1; // suppress key event
            }
            if (info.vkCode == NativeMethods.VK_F12 && current < Desktop.Count - 1)
            {
                Desktop.FromIndex(current + 1).MakeVisible();
                Console.WriteLine($"  -> switched to {current + 1}");
                return 1; // suppress key event
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [error] {ex.Message}");
        }
    }
    return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
};

var hookThread = new Thread(() =>
{
    hookThreadId = NativeMethods.GetCurrentThreadId();
    hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, callback, IntPtr.Zero, 0);
    if (hookHandle == IntPtr.Zero)
    {
        Console.Error.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        ready.Set();
        return;
    }
    ready.Set();

    while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
    {
        NativeMethods.TranslateMessage(ref msg);
        NativeMethods.DispatchMessage(ref msg);
    }

    NativeMethods.UnhookWindowsHookEx(hookHandle);
});
hookThread.Start();
ready.Wait();

if (hookHandle == IntPtr.Zero) return 1;

Console.WriteLine("F11: prev desktop  F12: next desktop  Ctrl-C: quit");
cts.Token.WaitHandle.WaitOne();

NativeMethods.PostThreadMessage(hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
hookThread.Join();
return 0;

static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const uint WM_QUIT = 0x0012;
    public const uint VK_F11 = 0x7A;
    public const uint VK_F12 = 0x7B;

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(IntPtr hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public nint wParam, lParam; public uint time; public int ptX, ptY; }
}
