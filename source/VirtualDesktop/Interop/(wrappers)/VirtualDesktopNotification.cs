using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using WindowsDesktop.Internal;

namespace WindowsDesktop.Interop
{
	[ComInterfaceWrapper(2)]
	[UsedImplicitly(ImplicitUseTargetFlags.Members)]
	public abstract class VirtualDesktopNotification
	{
		private VirtualDesktopCallbackMaterializer _materializer;
		private VirtualDesktopEventPipeline _pipeline;
		private long _providerEpoch;

		internal static VirtualDesktopNotification CreateInstance(ComInterfaceAssembly assembly, VirtualDesktopEventPipeline pipeline)
		{
			var type2 = assembly.GetType("VirtualDesktopNotificationListener2");
			if (type2 != null)
			{
				var instance = (VirtualDesktopNotification)Activator.CreateInstance(type2);
				instance.Initialize(assembly, pipeline);
				return instance;
			}
			else
			{
				var type = assembly.GetType("VirtualDesktopNotificationListener");
				var instance = (VirtualDesktopNotification)Activator.CreateInstance(type);
				instance.Initialize(assembly, pipeline);
				return instance;
			}
		}

		private void Initialize(ComInterfaceAssembly assembly, VirtualDesktopEventPipeline pipeline)
		{
			this._materializer = new VirtualDesktopCallbackMaterializer(assembly);
			this._pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
			this._providerEpoch = pipeline.CurrentProviderEpoch;
		}

		private void Capture(VirtualDesktopCallbackKind kind, Func<VirtualDesktopCallbackDto> capture)
		{
			VirtualDesktopCallbackDto dto;
			try { dto = capture(); }
			catch (Exception ex) { this._pipeline.ReportMaterializationFailure(kind, ex); return; }
			this._pipeline.Accept(dto.WithProviderEpoch(this._providerEpoch), this);
		}

		protected void VirtualDesktopCreatedCore(object pDesktop)
		{
			this.Capture(VirtualDesktopCallbackKind.Created, () => this._materializer.One(VirtualDesktopCallbackKind.Created, pDesktop));
		}

		protected void VirtualDesktopDestroyBeginCore(object pDesktopDestroyed, object pDesktopFallback)
		{
			this.Capture(VirtualDesktopCallbackKind.DestroyBegin, () => this._materializer.Two(VirtualDesktopCallbackKind.DestroyBegin, pDesktopDestroyed, pDesktopFallback));
		}

		protected void VirtualDesktopDestroyFailedCore(object pDesktopDestroyed, object pDesktopFallback)
		{
			this.Capture(VirtualDesktopCallbackKind.DestroyFailed, () => this._materializer.Two(VirtualDesktopCallbackKind.DestroyFailed, pDesktopDestroyed, pDesktopFallback));
		}

		protected void VirtualDesktopDestroyedCore(object pDesktopDestroyed, object pDesktopFallback)
		{
			this.Capture(VirtualDesktopCallbackKind.Destroyed, () => this._materializer.Two(VirtualDesktopCallbackKind.Destroyed, pDesktopDestroyed, pDesktopFallback));
		}

		protected void VirtualDesktopMovedCore(object pDesktop, int nFromIndex, int nToIndex)
		{
			this.Capture(VirtualDesktopCallbackKind.Moved, () => this._materializer.Move(pDesktop, nFromIndex, nToIndex));
		}

		protected void ViewVirtualDesktopChangedCore(object pView)
		{
			this.Capture(VirtualDesktopCallbackKind.ApplicationViewChanged, this._materializer.ApplicationViewChanged);
		}

		protected void CurrentVirtualDesktopChangedCore(object pDesktopOld, object pDesktopNew)
		{
			this.Capture(VirtualDesktopCallbackKind.CurrentChanged, () => this._materializer.Two(VirtualDesktopCallbackKind.CurrentChanged, pDesktopOld, pDesktopNew));
		}

		protected void VirtualDesktopRenamedCore(object pDesktop, HString chName)
		{
			this.Capture(VirtualDesktopCallbackKind.Renamed, () => this._materializer.Property(VirtualDesktopCallbackKind.Renamed, pDesktop, chName.ToManaged()));
		}

		protected void VirtualDesktopWallpaperChangedCore(object pDesktop, HString chPath)
		{
			this.Capture(VirtualDesktopCallbackKind.WallpaperChanged, () => this._materializer.Property(VirtualDesktopCallbackKind.WallpaperChanged, pDesktop, chPath.ToManaged()));
		}

		protected void VirtualDesktopSwitchedCore(object pDesktop)
		{
			this.Capture(VirtualDesktopCallbackKind.DesktopSwitched, () => this._materializer.One(VirtualDesktopCallbackKind.DesktopSwitched, pDesktop));
		}

		protected void RemoteVirtualDesktopConnectedCore(object pDesktop)
		{
			this.Capture(VirtualDesktopCallbackKind.RemoteDesktopConnected, () => this._materializer.One(VirtualDesktopCallbackKind.RemoteDesktopConnected, pDesktop));
		}
	}
}
