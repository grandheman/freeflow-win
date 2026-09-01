using System;
using System.Runtime.InteropServices;

namespace FreeFlow.App.Platform.Input;

/// <summary>
/// Minimal Win32 message pump for the dedicated keyboard-hook thread.
/// </summary>
/// <remarks>
/// A low-level keyboard hook is only delivered to a thread that pumps messages, and
/// the hook thread deliberately has no window, so the normal WPF dispatcher is not
/// available here.
/// </remarks>
internal static class MessageLoop
{
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static uint CurrentThreadId => GetCurrentThreadId();

    /// <summary>Pumps until <see cref="PostQuit"/> is called for this thread.</summary>
    public static void Run()
    {
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public static void PostQuit(uint threadId)
        => PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
}
