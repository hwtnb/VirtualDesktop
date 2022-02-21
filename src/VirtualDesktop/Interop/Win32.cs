using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowsDesktop.Interop;

[ComImport]
[Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(in Guid guidService, in Guid riid);
}

[ComImport]
[Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectArray
{
    uint GetCount();

    [return: MarshalAs(UnmanagedType.Interface)]
    object GetAt(uint iIndex, in Guid riid);
}

[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);

    Guid GetWindowDesktopId(IntPtr topLevelWindow);

    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

internal static class PInvoke
{
    [DllImport("user32.dll")]
    public static extern bool CloseWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpProcName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, MessageFilter action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);
}

internal enum MessageFilter : uint
{
    MSGFLT_RESET = 0,
    MSGFLT_ALLOW = 1,
    MSGFLT_DISALLOW = 2
};

internal enum MessageFilterInfo : uint
{
    MSGFLTINFO_NONE = 0,
    MSGFLTINFO_ALREADYALLOWED_FORWND = 1,
    MSGFLTINFO_ALREADYDISALLOWED_FORWND = 2,
    MSGFLTINFO_ALLOWED_HIGHER = 3,
};

[StructLayout(LayoutKind.Sequential)]
internal struct CHANGEFILTERSTRUCT
{
    public uint cbSize;
    public MessageFilterInfo ExtStatus;
}
