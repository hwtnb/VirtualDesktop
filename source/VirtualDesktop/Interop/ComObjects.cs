﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowsDesktop.Internal;
using WindowsDesktop.Properties;

namespace WindowsDesktop.Interop
{
	internal class ComObjects : IDisposable
	{
		private readonly ComInterfaceAssembly _assembly;
		private ExplorerRestartListenerWindow _listenerWindow;
		private IDisposable _listener;

		public IVirtualDesktopManager VirtualDesktopManager { get; private set; }

		public VirtualDesktopManagerInternal VirtualDesktopManagerInternal { get; private set; }

		public VirtualDesktopNotificationService VirtualDesktopNotificationService { get; private set; }

		public VirtualDesktopPinnedApps VirtualDesktopPinnedApps { get; private set; }

		public ApplicationViewCollection ApplicationViewCollection { get; private set; }

		public bool IsAvailable { get; private set; } = false;

		public ComObjects(ComInterfaceAssembly assembly)
		{
			this._assembly = assembly;
			this.Initialize();
		}

		public void Listen()
		{
			this._listenerWindow = new ExplorerRestartListenerWindow(() => this.Initialize());
			this._listenerWindow.Show();
		}

		private void Initialize()
		{
			this.IsAvailable = false;
			VirtualDesktop.ClearCaches();
			VirtualDesktopCache.Initialize(this._assembly);

			this.VirtualDesktopManager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID.VirtualDesktopManager));
			if (ProductInfo.OSBuild >= 26100)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal26100(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 25931)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal25931(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 25158)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal25158(this._assembly);
			}
			else if (ProductInfo.OSBuild > 22621 || (ProductInfo.OSBuild == 22621 && ProductInfo.OSRevision >= 2215))
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal22621(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 22449)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal22449(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 21359)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal21359(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 21313)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal21313(this._assembly);
			}
			else if (ProductInfo.OSBuild >= 20231)
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal20231(this._assembly);
			}
			else
			{
				this.VirtualDesktopManagerInternal = new VirtualDesktopManagerInternal10240(this._assembly);
			}
			this.VirtualDesktopNotificationService = new VirtualDesktopNotificationService(this._assembly);
			this.VirtualDesktopPinnedApps = new VirtualDesktopPinnedApps(this._assembly);
			this.ApplicationViewCollection = new ApplicationViewCollection(this._assembly);

			this._listener?.Dispose();
			this._listener = this.VirtualDesktopNotificationService.Register(VirtualDesktopNotification.CreateInstance(this._assembly));
			this.IsAvailable = true;
		}

		public void Dispose()
		{
			this._listener?.Dispose();
			this._listenerWindow?.Close();
		}

		private class ExplorerRestartListenerWindow : TransparentWindow
		{
			private uint _explorerRestartedMessage;
			private readonly Action _action;

			public ExplorerRestartListenerWindow(Action action)
			{
				this.Name = nameof(ExplorerRestartListenerWindow);
				this._action = action;
			}

			public override void Show()
			{
				base.Show();
				this._explorerRestartedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

				var filterStatus = new CHANGEFILTERSTRUCT();
				filterStatus.cbSize = (uint)Marshal.SizeOf(filterStatus);
				filterStatus.ExtStatus = MessageFilterInfo.MSGFLTINFO_NONE;
				NativeMethods.ChangeWindowMessageFilterEx(this.Handle, this._explorerRestartedMessage, (uint)MessageFilter.MSGFLT_ALLOW, ref filterStatus);
			}

			protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
			{
				if (msg == this._explorerRestartedMessage)
				{
					this._action();
					return IntPtr.Zero;
				}

				return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
			}
		}
	}
}
