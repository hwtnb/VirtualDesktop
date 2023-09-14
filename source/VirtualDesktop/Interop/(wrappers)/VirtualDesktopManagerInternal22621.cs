using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WindowsDesktop.Interop
{
	[ComInterfaceWrapper("IVirtualDesktopManagerInternal", 2, 22621)]
	internal class VirtualDesktopManagerInternal22621 : VirtualDesktopManagerInternal
	{
		public VirtualDesktopManagerInternal22621(ComInterfaceAssembly assembly, uint latestVersion = 2)
			: base(assembly, latestVersion)
		{
		}

		public override int GetCount()
		{
			return this.Invoke<int>(Args());
		}

		public override void MoveViewToDesktop(ApplicationView pView, VirtualDesktop desktop)
		{
			this.Invoke(Args(pView.ComObject, desktop.ComObject));
		}

		public override VirtualDesktop GetCurrentDesktop()
		{
			return this.GetDesktop(Args());
		}

		public override IEnumerable<VirtualDesktop> GetDesktops()
		{
			var array = this.Invoke<IObjectArray>(Args());
			var count = array.GetCount();
			var vdType = this.ComInterfaceAssembly.GetType("IVirtualDesktop");

			for (var i = 0u; i < count; i++)
			{
				array.GetAt(i, vdType.GUID, out var ppvObject);
				yield return VirtualDesktopCache.GetOrCreate(ppvObject);
			}
		}

		public override VirtualDesktop GetAdjacentDesktop(VirtualDesktop pDesktopReference, AdjacentDesktop uDirection)
		{
			return this.GetDesktop(Args(pDesktopReference.ComObject, uDirection));
		}

		public override void SwitchDesktop(VirtualDesktop desktop)
		{
			this.Invoke(Args(desktop.ComObject));
		}

		public override VirtualDesktop CreateDesktopW()
		{
			return this.GetDesktop(Args());
		}

		public override void MoveDesktop(VirtualDesktop desktop, int index)
		{
			this.Invoke(Args(desktop.ComObject, index));
		}

		public override void RemoveDesktop(VirtualDesktop pRemove, VirtualDesktop pFallbackDesktop)
		{
			this.Invoke(Args(pRemove.ComObject, pFallbackDesktop.ComObject));
		}

		public override VirtualDesktop FindDesktop(Guid desktopId)
		{
			return this.GetDesktop(Args(desktopId));
		}

		private VirtualDesktop GetDesktop(object[] parameters = null, [CallerMemberName] string methodName = "")
			=> VirtualDesktopCache.GetOrCreate(this.Invoke<object>(parameters, methodName));

		public override void SetDesktopName(VirtualDesktop desktop, string name)
		{
			this.Invoke(Args(desktop.ComObject, name));
		}

		public override void SetDesktopWallpaper(VirtualDesktop desktop, string path)
		{
			this.Invoke(Args(desktop.ComObject, path));
		}
	}
}
