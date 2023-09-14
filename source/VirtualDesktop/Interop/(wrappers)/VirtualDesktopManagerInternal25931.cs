using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WindowsDesktop.Interop
{
	[ComInterfaceWrapper("IVirtualDesktopManagerInternal", 2, 25931)]
	internal sealed class VirtualDesktopManagerInternal25931 : VirtualDesktopManagerInternal22621
	{
		public VirtualDesktopManagerInternal25931(ComInterfaceAssembly assembly)
			: base(assembly, latestVersion: 2)
		{
		}
	}
}
