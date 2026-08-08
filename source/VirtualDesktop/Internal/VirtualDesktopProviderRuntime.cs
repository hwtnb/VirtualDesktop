using System;
using System.Collections.Generic;
using WindowsDesktop.Interop;

namespace WindowsDesktop.Internal
{
	internal interface IVirtualDesktopProviderRuntime
	{
		bool TryResolveDesktop(Guid id, bool managedOnly, out VirtualDesktop desktop);
		void RegisterDesktop(VirtualDesktop desktop);
		void RemoveDesktop(Guid id);
		void SetDesktopName(VirtualDesktop desktop, string value);
		void SetDesktopWallpaper(VirtualDesktop desktop, string value);
	}

	internal interface IVirtualDesktopProviderRuntimeReset
	{
		void ResetManagedDesktops();
	}

	internal sealed class ComVirtualDesktopProviderRuntime : IVirtualDesktopProviderRuntime, IVirtualDesktopProviderRuntimeReset
	{
		private readonly object _gate = new object();
		private readonly Dictionary<Guid, VirtualDesktop> _managedDesktops = new Dictionary<Guid, VirtualDesktop>();
		private readonly VirtualDesktopProvider _provider;

		internal ComVirtualDesktopProviderRuntime(VirtualDesktopProvider provider)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
		}

		public bool TryResolveDesktop(Guid id, bool managedOnly, out VirtualDesktop desktop)
		{
			lock (this._gate)
			{
				if (this._managedDesktops.TryGetValue(id, out desktop)) return true;
			}
			if (managedOnly) { desktop = null; return false; }
			desktop = this._provider.ComObjects.VirtualDesktopManagerInternal.FindDesktop(id);
			if (desktop == null) return false;
			this.RegisterDesktop(desktop);
			return true;
		}

		public void RegisterDesktop(VirtualDesktop desktop)
		{
			if (desktop == null) throw new ArgumentNullException(nameof(desktop));
			lock (this._gate) this._managedDesktops[desktop.Id] = desktop;
		}

		public void RemoveDesktop(Guid id)
		{
			lock (this._gate) this._managedDesktops.Remove(id);
		}

		public void ResetManagedDesktops()
		{
			lock (this._gate) this._managedDesktops.Clear();
		}

		public void SetDesktopName(VirtualDesktop desktop, string value)
		{
			using (var marshaler = HString.CreateMarshaler(value))
				this._provider.ComObjects.VirtualDesktopManagerInternal.SetDesktopName(desktop, marshaler.Value);
		}

		public void SetDesktopWallpaper(VirtualDesktop desktop, string value)
		{
			using (var marshaler = HString.CreateMarshaler(value))
				this._provider.ComObjects.VirtualDesktopManagerInternal.SetDesktopWallpaper(desktop, marshaler.Value);
		}
	}
}
