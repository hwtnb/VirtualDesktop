using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WindowsDesktop.Interop;

namespace WindowsDesktop
{
	partial class VirtualDesktop
	{
		private static bool? _isSupported = null;

		/// <summary>
		/// Gets a value indicating virtual desktops are supported by the host.
		/// </summary>
		public static bool IsSupported => GetIsSupported();

		/// <summary>
		/// Gets the virtual desktop that is currently displayed.
		/// </summary>
		public static VirtualDesktop Current
		{
			get
			{
				VirtualDesktopHelper.ThrowIfNotSupported();

				return ComInterface.VirtualDesktopManagerInternal.GetCurrentDesktop();
			}
		}

		/// <summary>
		/// Gets history of virtual desktops that were displayed.
		/// </summary>
		public static VirtualDesktopHistory History { get; } = new VirtualDesktopHistory();

		/// <summary>
		/// Returns an array of available virtual desktops.
		/// </summary>
		public static VirtualDesktop[] GetDesktops()
		{
			VirtualDesktopHelper.ThrowIfNotSupported();

			return ComInterface.VirtualDesktopManagerInternal.GetDesktops().ToArray();
		}

		/// <summary>
		/// Returns a new virtual desktop.
		/// </summary>
		public static VirtualDesktop Create()
		{
			VirtualDesktopHelper.ThrowIfNotSupported();

			return ComInterface.VirtualDesktopManagerInternal.CreateDesktopW();
		}

		/// <summary>
		/// Returns a virtual desktop matching the specified identifier.
		/// </summary>
		/// <param name="desktopId">The identifier of the virtual desktop to return.</param>
		/// <remarks>Returns null if the identifier is not associated with any available desktops.</remarks>
		public static VirtualDesktop FromId(Guid desktopId)
		{
			VirtualDesktopHelper.ThrowIfNotSupported();

			try
			{
				return ComInterface.VirtualDesktopManagerInternal.FindDesktop(desktopId);
			}
			catch (COMException ex) when (ex.Match(HResult.TYPE_E_ELEMENTNOTFOUND))
			{
				return null;
			}
		}

		/// <summary>
		/// Returns the virtual desktop the window is located on.
		/// </summary>
		/// <param name="hWnd">The handle of the window.</param>
		/// <remarks>Returns null if the handle is not associated with any open windows.</remarks>
		public static VirtualDesktop FromHwnd(IntPtr hWnd)
		{
			VirtualDesktopHelper.ThrowIfNotSupported();

			if (hWnd == IntPtr.Zero) return null;

			try
			{
				var desktopId = ComInterface.VirtualDesktopManager.GetWindowDesktopId(hWnd);
				return ComInterface.VirtualDesktopManagerInternal.FindDesktop(desktopId);
			}
			catch (COMException ex) when (ex.Match(HResult.REGDB_E_CLASSNOTREG, HResult.TYPE_E_ELEMENTNOTFOUND))
			{
				return null;
			}
		}

		internal static bool GetIsSupported()
		{
			return _isSupported ?? (_isSupported = Core()).Value;

			bool Core()
			{
#if DEBUG
				if (Environment.OSVersion.Version.Major < 10) return false;
#endif
				try
				{
					ProviderInternal.Initialize().Wait();
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine("VirtualDesktop initialization error:");
					System.Diagnostics.Debug.WriteLine(ex);

					return false;
				}

				return true;
			}
		}
	}

	public class VirtualDesktopHistory : IDisposable
	{
		public bool IsInitialized => this._list != null;

		public int Count => this.List.Count;

		public VirtualDesktop Previous => this.Count > 0 ? this._list[0] : null;

		private List<VirtualDesktop> _list = null;

		private List<VirtualDesktop> List
		{
			get
			{
				if (!this.IsInitialized) this.Initialize();
				return this._list;
			}
		}

		internal VirtualDesktopHistory()
		{
			VirtualDesktop.Created += this.OnCreated;
			VirtualDesktop.Destroyed += this.OnDestroyed;
			VirtualDesktop.CurrentChanged += this.OnCurrentChanged;
		}

		internal void Clear()
		{
			this._list = null;
		}

		private void Initialize()
		{
			var current = VirtualDesktop.Current;
			this._list = VirtualDesktop.GetDesktops().ToList();
			this.SetPrevious(current);
		}

		private void SetPrevious(VirtualDesktop prev)
		{
			var list = this.List;
			var oldIndex = list.IndexOf(prev);
			for (var i = oldIndex; i > 0; i--)
			{
				list[i] = list[i - 1];
			}
			list[0] = prev;
		}

		private void Add(VirtualDesktop desktop)
		{
			this.List.Add(desktop);
		}

		private void Remove(VirtualDesktop desktop)
		{
			this.List.Remove(desktop);
		}

		private void OnCreated(object sender, VirtualDesktop newDesktop)
		{
			this.Add(newDesktop);
		}

		private void OnDestroyed(object sender, VirtualDesktopDestroyEventArgs e)
		{
			this.Remove(e.Destroyed);
		}

		private void OnCurrentChanged(object sender, VirtualDesktopChangedEventArgs e)
		{
			this.SetPrevious(e.OldDesktop);
		}

		public void Dispose()
		{
			VirtualDesktop.Created -= this.OnCreated;
			VirtualDesktop.Destroyed -= this.OnDestroyed;
			VirtualDesktop.CurrentChanged -= this.OnCurrentChanged;

			this.Clear();
		}
	}
}
