using System;
using System.Runtime.InteropServices;

namespace WindowsDesktop.Interop
{
	[ComImport]
	[Guid("00000000-0000-0000-0000-000000000000") /* replace at runtime */]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IVirtualDesktopManagerInternal
	{
		int GetCount();

		void MoveViewToDesktop(IApplicationView pView, IVirtualDesktop pDesktop);

		[return: MarshalAs(UnmanagedType.Bool)]
		bool CanViewMoveDesktops(IApplicationView pView);

		IVirtualDesktop GetCurrentDesktop();

		IObjectArray GetDesktops();

		IVirtualDesktop GetAdjacentDesktop(IVirtualDesktop pDesktopReference, AdjacentDesktop uDirection);

		void SwitchDesktop(IVirtualDesktop pDesktop);

		void SwitchDesktopAndMoveForegroundView(IVirtualDesktop pDesktop);//new

		IVirtualDesktop CreateDesktopW();

		IVirtualDesktop MoveDesktop(IVirtualDesktop pDesktopReference, int nIndex);

		void RemoveDesktop(IVirtualDesktop pRemove, IVirtualDesktop pFallbackDesktop);

		IVirtualDesktop FindDesktop([In, MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);

		void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray includeViews, out IObjectArray excludeViews);//15

		void SetDesktopName(IVirtualDesktop pDesktop, [MarshalAs(UnmanagedType.HString)] string chName);

		void SetDesktopWallpaper(IVirtualDesktop pDesktop, [MarshalAs(UnmanagedType.HString)] string chPath);

		void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string wallpaper);//18

		void CopyDesktopState(IApplicationView pView0, IApplicationView pView1);//19
	}
}
