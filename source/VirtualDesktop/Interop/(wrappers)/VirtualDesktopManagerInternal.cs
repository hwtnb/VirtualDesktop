﻿using System;
using System.Collections.Generic;

namespace WindowsDesktop.Interop
{
	internal abstract class VirtualDesktopManagerInternal : ComInterfaceWrapperBase
	{
		public VirtualDesktopManagerInternal(ComInterfaceAssembly assembly, uint latestVersion = 1)
			: base(assembly, "IVirtualDesktopManagerInternal", latestVersion, service: CLSID.VirtualDesktopAPIUnknown)
		{
		}

		public abstract int GetCount();

		public abstract void MoveViewToDesktop(ApplicationView pView, VirtualDesktop desktop);

		public abstract VirtualDesktop GetCurrentDesktop();

		public abstract IEnumerable<VirtualDesktop> GetDesktops();

		public abstract VirtualDesktop GetAdjacentDesktop(VirtualDesktop pDesktopReference, AdjacentDesktop uDirection);

		public abstract void SwitchDesktop(VirtualDesktop desktop);

		public abstract VirtualDesktop CreateDesktopW();

		public abstract void MoveDesktop(VirtualDesktop desktop, int index);

		public abstract void RemoveDesktop(VirtualDesktop pRemove, VirtualDesktop pFallbackDesktop);

		public abstract VirtualDesktop FindDesktop(Guid desktopId);

		public abstract void SetDesktopName(VirtualDesktop desktop, HString name);

		public abstract void SetDesktopWallpaper(VirtualDesktop desktop, HString path);
	}
}
