using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WindowsDesktop.Properties;

namespace WindowsDesktop.Interop
{
	internal static class NativeMethods
	{
		public static readonly IntPtr WA_ACTIVE = new IntPtr(1);
		public static readonly IntPtr WA_INACTIVE = new IntPtr(0);
		public static readonly string TaskbarClassName = "Shell_TrayWnd";
		public static readonly string TaskViewClassName = ProductInfo.OSBuild >= 22000 ? "XamlExplorerHostIslandWindow" : "Windows.UI.Core.CoreWindow";
		public static readonly string ImmersiveShellClassName = ProductInfo.OSBuild >= 22000 ? "ApplicationManager_ImmersiveShellWindow" : "ApplicationManager_DesktopShellWindow";

		public delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

		[DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
		public static extern IntPtr SendMessage(IntPtr hWnd, WindowsMessages msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		public static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool BringWindowToTop(IntPtr hWnd);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

		[DllImport("user32.dll")]
		public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		public static extern uint RegisterWindowMessage(string lpProcName);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, ref CHANGEFILTERSTRUCT pChangeFilterStruct);

		[DllImport("user32.dll")]
		public static extern bool CloseWindow(IntPtr hWnd);

		public static string GetClassName(IntPtr handle)
		{
			var buffer = new StringBuilder(256 + 1);
			GetClassName(handle, buffer, buffer.Capacity);
			return buffer.ToString();
		}

		public static bool ForceSetForegroundWindow(IntPtr targetHandle, IntPtr foregroundHandle)
		{
			var targetThreadId = GetWindowThreadProcessId(targetHandle, out _);
			var foregroundThreadId = GetWindowThreadProcessId(foregroundHandle, out _);

			var isForegroundChanged = false;
			if (targetThreadId == foregroundThreadId)
			{
				isForegroundChanged = SetForegroundWindow(targetHandle);
				var isTopWindow = BringWindowToTop(targetHandle);
				return isForegroundChanged && isTopWindow;
			}

			var isAttached = AttachThreadInput(targetThreadId, foregroundThreadId, true);
			try
			{
				SendMessage(foregroundHandle, WindowsMessages.WM_ACTIVATE, WA_INACTIVE, foregroundHandle);
				SendMessage(targetHandle, WindowsMessages.WM_ACTIVATE, WA_ACTIVE, targetHandle);
				SendMessage(targetHandle, WindowsMessages.WM_SETFOCUS, targetHandle, IntPtr.Zero);
				isForegroundChanged = BringWindowToTop(targetHandle);
			}
			finally
			{
				if (isAttached)
				{
					AttachThreadInput(targetThreadId, foregroundThreadId, false);
				}
			}
			return isForegroundChanged;
		}

		public static IntPtr ForceSendActivationMessage(IntPtr targetHandle, IntPtr foregroundHandle)
		{
			var targetThreadId = GetWindowThreadProcessId(targetHandle, out _);
			var foregroundThreadId = GetWindowThreadProcessId(foregroundHandle, out _);

			if (targetThreadId == foregroundThreadId)
			{
				return SendMessage(targetHandle, WindowsMessages.WM_ACTIVATE, WA_ACTIVE, targetHandle);
			}

			var result = IntPtr.Zero;
			var isAttached = AttachThreadInput(targetThreadId, foregroundThreadId, true);
			try
			{
				result = SendMessage(targetHandle, WindowsMessages.WM_ACTIVATE, WA_ACTIVE, targetHandle);
			}
			finally
			{
				if (isAttached)
				{
					AttachThreadInput(targetThreadId, foregroundThreadId, false);
				}
			}
			return result;
		}
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
