﻿using System;
using System.Runtime.InteropServices;

namespace WindowsDesktop.Interop
{
	[ComImport]
	[Guid("00000000-0000-0000-0000-000000000000") /* replace at runtime */]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IVirtualDesktopManagerInternal
	{
		int GetCount(IntPtr hWndOrMon);

		void MoveViewToDesktop(IApplicationView pView, IVirtualDesktop pDesktop);

		[return: MarshalAs(UnmanagedType.Bool)]
		bool CanViewMoveDesktops(IApplicationView pView);

		IVirtualDesktop GetCurrentDesktop(IntPtr hWndOrMon);

		IObjectArray GetAllCurrentDesktops();//7

		IObjectArray GetDesktops(IntPtr hWndOrMon);

		IVirtualDesktop GetAdjacentDesktop(IVirtualDesktop pDesktopReference, AdjacentDesktop uDirection);

		void SwitchDesktop(IntPtr hWndOrMon, IVirtualDesktop pDesktop);

		void SwitchDesktopAndMoveForegroundView(IntPtr hWndOrMon, IVirtualDesktop pDesktop);//new

		IVirtualDesktop CreateDesktopW(IntPtr hWndOrMon);

		IVirtualDesktop MoveDesktop(IVirtualDesktop pDesktopReference, IntPtr hWndOrMon, int nIndex);

		void RemoveDesktop(IVirtualDesktop pRemove, IVirtualDesktop pFallbackDesktop);

		IVirtualDesktop FindDesktop([In, MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);

		void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray includeViews, out IObjectArray excludeViews);//15

		void SetDesktopName(IVirtualDesktop pDesktop, [MarshalAs(UnmanagedType.HString)] string chName);

		void SetDesktopWallpaper(IVirtualDesktop pDesktop, [MarshalAs(UnmanagedType.HString)] string chPath);

		void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string wallpaper);//18

		void CopyDesktopState(IApplicationView pView0, IApplicationView pView1);//19

		int GetDesktopIsPerMonitor();//20

		void SetDesktopIsPerMonitor(int state);//21
	}
}
