using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WindowsDesktop.Interop
{
	internal static class NativeMethods
	{
		[DllImport("user32.dll")]
		public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		public static extern uint RegisterWindowMessage(string lpProcName);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);

		[DllImport("user32.dll")]
		public static extern bool CloseWindow(IntPtr hWnd);
	}

	public enum MessageFilter : uint
	{
		MSGFLT_RESET = 0,
		MSGFLT_ALLOW = 1,
		MSGFLT_DISALLOW = 2
	};

	public enum MessageFilterInfo : uint
	{
		MSGFLTINFO_NONE = 0,
		MSGFLTINFO_ALREADYALLOWED_FORWND = 1,
		MSGFLTINFO_ALREADYDISALLOWED_FORWND = 2,
		MSGFLTINFO_ALLOWED_HIGHER = 3,
	};

	[StructLayout(LayoutKind.Sequential)]
	public struct CHANGEFILTERSTRUCT
	{
		public uint cbSize;
		public MessageFilterInfo ExtStatus;
	}
}
