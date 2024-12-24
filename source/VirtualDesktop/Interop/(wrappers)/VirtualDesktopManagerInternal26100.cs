using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WindowsDesktop.Interop
{
	[ComInterfaceWrapper("IVirtualDesktopManagerInternal", 2, 26100)]
	internal sealed class VirtualDesktopManagerInternal26100 : VirtualDesktopManagerInternal22621
	{
		public VirtualDesktopManagerInternal26100(ComInterfaceAssembly assembly)
			: base(assembly, latestVersion: 2)
		{
		}
	}
}
