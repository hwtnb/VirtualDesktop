using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop;
using WindowsDesktop.Properties;
using JetBrains.Annotations;

namespace WindowsDesktop
{
	/// <summary>
	/// Encapsulates a Windows 10 virtual desktop.
	/// </summary>
	[ComInterfaceWrapper(2)]
	[DebuggerDisplay("{Id}")]
	[UsedImplicitly(ImplicitUseTargetFlags.Members)]
	public partial class VirtualDesktop : ComInterfaceWrapperBase, IDisposable
	{
		private VirtualDesktopProvider _provider;
		/// <summary>
		/// Gets the unique identifier for this virtual desktop.
		/// </summary>
		public Guid Id { get; }

		/// <summary>
		/// Gets the index for the virtual desktop.
		/// </summary>
		public int Index
		{
			get
			{
				var desktops = AllDesktops;
				var index = Array.IndexOf(desktops, this);
				return index;
			}
		}

		private string _name = null;

		/// <summary>
		/// Gets the name for the virtual desktop.
		/// </summary>
		public string Name
		{
			get => this._name;
			set => this.ProviderOwner.SetDesktopName(this, value);
		}

		private string _wallpaperPath = null;

		/// <summary>
		/// Gets the name for the virtual desktop.
		/// </summary>
		public string WallpaperPath
		{
			get => this._wallpaperPath;
			set => this.ProviderOwner.SetDesktopWallpaper(this, value);
		}

		[UsedImplicitly]
		internal VirtualDesktop(VirtualDesktopProvider provider, ComInterfaceAssembly assembly, Guid id, object comObject)
			: base(assembly, comObject, latestVersion: 2)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
			this.Id = id;
			this._provider.RegisterDesktop(this);

			if (ProductInfo.OSBuild >= 20231 || this.ComVersion >= 2)
			{
				this._name = this.Invoke<HString>(Args(), "GetName");

				if (ProductInfo.OSBuild >= 21313)
				{
					this._wallpaperPath = this.Invoke<HString>(Args(), "GetWallpaperPath");
				}
			}
		}

		private VirtualDesktopProvider ProviderOwner => this._provider ?? ProviderInternal;

		/// <summary>
		/// Switches to this virtual desktop.
		/// </summary>
		public void Switch()
		{
			var current = ComInterface.VirtualDesktopManagerInternal.GetCurrentDesktop();
			if (this == current) return;

			var currentHandle = NativeMethods.GetForegroundWindow();

			// When the current foreground window is a special window (none / pinned / taskbar /
			// Task View), the OS default switch behavior is appropriate and the focus fix below
			// is unnecessary (or would target the wrong window). Just switch.
			if (currentHandle == IntPtr.Zero
				|| IsPinnedWindowOrDefault(currentHandle)
				|| currentHandle == VirtualDesktopCache.TaskbarHandle
				|| NativeMethods.GetClassName(currentHandle) == NativeMethods.TaskViewClassName)
			{
				ComInterface.VirtualDesktopManagerInternal.SwitchDesktop(this);
				return;
			}

			var immersiveShellHandle = VirtualDesktopCache.ImmersiveShellHandle;
			if (immersiveShellHandle != IntPtr.Zero)
			{
				NativeMethods.ForceSendActivationMessage(immersiveShellHandle, currentHandle);
			}

			ComInterface.VirtualDesktopManagerInternal.SwitchDesktop(this);

			// The target desktop is now current, so its windows are at the front of the Z-order.
			// Searching here (instead of before the switch) lets EnumWindows short-circuit on the
			// first hit, and the per-window desktop lookup runs only for real app windows.
			var targetHandle = this.GetFirstWindowOnDesktop();
			if (targetHandle != IntPtr.Zero)
			{
				var foregroundHandle = NativeMethods.GetForegroundWindow();
				if (targetHandle != foregroundHandle)
				{
					NativeMethods.ForceSetForegroundWindow(targetHandle, foregroundHandle);
				}
			}
		}

		/// <summary>
		/// Moves this virtual desktop to a new location.
		/// </summary>
		/// <param name="index">The zero-based index specifying the new location of the virtual desktop.</param>
		public void Move(int index)
		{
			ComInterface.VirtualDesktopManagerInternal.MoveDesktop(this, index);
		}

		/// <summary>
		/// Removes this virtual desktop and switches to an available one.
		/// </summary>
		/// <remarks>If this is the last virtual desktop, a new one will be created to switch to.</remarks>
		public void Remove()
		{
			var fallback = ComInterface.VirtualDesktopManagerInternal.GetDesktops().FirstOrDefault(x => x.Id != this.Id) ?? Create();
			this.Remove(fallback);
		}

		/// <summary>
		/// Removes this virtual desktop and switches to <paramref name="fallbackDesktop" />.
		/// </summary>
		/// <param name="fallbackDesktop">A virtual desktop to be displayed after the virtual desktop is removed.</param>
		public void Remove(VirtualDesktop fallbackDesktop)
		{
			if (fallbackDesktop == null) throw new ArgumentNullException(nameof(fallbackDesktop));

			ComInterface.VirtualDesktopManagerInternal.RemoveDesktop(this, fallbackDesktop);
		}

		/// <summary>
		/// Returns the adjacent virtual desktop on the left, or null if there isn't one.
		/// </summary>
		public VirtualDesktop GetLeft()
		{
			try
			{
				return ComInterface.VirtualDesktopManagerInternal.GetAdjacentDesktop(this, AdjacentDesktop.LeftDirection);
			}
			catch (COMException ex) when (ex.Match(HResult.TYPE_E_OUTOFBOUNDS))
			{
				return null;
			}
		}

		/// <summary>
		/// Returns the adjacent virtual desktop on the right, or null if there isn't one.
		/// </summary>
		public VirtualDesktop GetRight()
		{
			try
			{
				return ComInterface.VirtualDesktopManagerInternal.GetAdjacentDesktop(this, AdjacentDesktop.RightDirection);
			}
			catch (COMException ex) when (ex.Match(HResult.TYPE_E_OUTOFBOUNDS))
			{
				return null;
			}
		}

		private IntPtr GetFirstWindowOnDesktop()
		{
			var handle = IntPtr.Zero;
			_ = NativeMethods.EnumWindows(FindFirstWindowOnThisDesktop, IntPtr.Zero);
			return handle;

			bool FindFirstWindowOnThisDesktop(IntPtr hWnd, IntPtr lParam)
			{
				// Cheap user32-only checks first, so the cross-process desktop lookup below is
				// paid only for genuine top-level app windows rather than every top-level window.
				if (!NativeMethods.IsFocusableTopLevelWindow(hWnd)) return true;

				if (this.IsWindowOnThisDesktop(hWnd))
				{
					handle = hWnd;
					return false;
				}
				return true;
			}
		}

		private bool IsWindowOnThisDesktop(IntPtr hWnd)
		{
			try
			{
				// Compare the desktop id directly instead of materializing a VirtualDesktop via
				// FromHwnd (which costs an extra FindDesktop COM call plus a cache allocation).
				return ComInterface.VirtualDesktopManager.GetWindowDesktopId(hWnd) == this.Id;
			}
			catch (COMException)
			{
				return false;
			}
		}

		internal void CommitNameMirror(string name) => this._name = name;

		internal void CommitWallpaperMirror(string path) => this._wallpaperPath = path;

		#region IDisposable
		private bool _disposed = false;

		/// <summary>
		/// Disposes of this <see cref="VirtualDesktop"/>.
		/// </summary>
		/// <param name="disposeOfManagedObjects">If <see langword="true"/>, disposes of managed objects.</param>
		protected virtual void Dispose(bool disposeOfManagedObjects)
		{
			if (!this._disposed)
			{
				if (disposeOfManagedObjects)
				{
					this.Remove();
				}

				this._disposed = true;
			}
		}

		/// <summary>
		/// Disposes of this <see cref="VirtualDesktop"/>.
		/// </summary>
		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}
		#endregion
	}
}
