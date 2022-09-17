using System;
// ReSharper disable InconsistentNaming

namespace WindowsDesktop.Interop
{
	/// <summary>
	/// Windows Messages
	/// Defined in winuser.h from Windows SDK v6.1
	/// Documentation pulled from MSDN.
	/// </summary>
	public enum WindowsMessages : uint
	{
		/// <summary>
		/// The WM_ACTIVATE message is sent to both the window being activated and the window being deactivated. If the windows use the same input queue, the message is sent synchronously, first to the window procedure of the top-level window being deactivated, then to the window procedure of the top-level window being activated. If the windows use different input queues, the message is sent asynchronously, so the window is activated immediately. 
		/// </summary>
		WM_ACTIVATE = 0x0006,

		/// <summary>
		/// The WM_SETFOCUS message is sent to a window after it has gained the keyboard focus. 
		/// </summary>
		WM_SETFOCUS = 0x0007,
	}
}
