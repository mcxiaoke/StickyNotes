using System;
using System.Runtime.InteropServices;

namespace StickyNotes.Infrastructure;

/// <summary>
/// Win32 原生 API 互操作封装
/// </summary>
internal static class NativeMethods
{
    public const int HWND_BROADCAST = 0xffff;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static readonly int WM_ACTIVATE_INSTANCE = RegisterWindowMessage("StickyNotes_Activate_MainWindow");

    public static void NotifyExistingInstance()
    {
        if (WM_ACTIVATE_INSTANCE != 0)
        {
            PostMessage((IntPtr)HWND_BROADCAST, WM_ACTIVATE_INSTANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
