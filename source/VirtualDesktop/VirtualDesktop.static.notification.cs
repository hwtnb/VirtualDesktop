using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WindowsDesktop.Interop;
using WindowsDesktop.Internal;

namespace WindowsDesktop
{
	partial class VirtualDesktop
	{
		/// <summary>
		/// Occurs when a virtual desktop is created.
		/// </summary>
		public static event EventHandler<VirtualDesktop> Created;

		public static event EventHandler<VirtualDesktopDestroyEventArgs> DestroyBegin;

		public static event EventHandler<VirtualDesktopDestroyEventArgs> DestroyFailed;

		/// <summary>
		/// Occurs when a virtual desktop is destroyed.
		/// </summary>
		public static event EventHandler<VirtualDesktopDestroyEventArgs> Destroyed;

		[EditorBrowsable(EditorBrowsableState.Never)]
		public static event EventHandler ApplicationViewChanged;

		/// <summary>
		/// Occurs when the current virtual desktop is changed.
		/// </summary>
		public static event EventHandler<VirtualDesktopChangedEventArgs> CurrentChanged;

		/// <summary>
		/// Occurs when a virtual desktop is moved.
		/// </summary>
		public static event EventHandler<VirtualDesktopMovedEventArgs> Moved;

		/// <summary>
		/// Occurs when a virtual desktop is renamed.
		/// </summary>
		public static event EventHandler<VirtualDesktopRenamedEventArgs> Renamed;

		/// <summary>
		/// Occurs when the wallpaper in the virtual desktop is changed.
		/// </summary>
		public static event EventHandler<VirtualDesktopWallpaperChangedEventArgs> WallpaperChanged;

		/// <summary>
		/// Occurs when a virtual desktop is switched.
		/// </summary>
		public static event EventHandler<VirtualDesktop> DesktopSwitched;

		/// <summary>
		/// Occurs when a remote virtual desktop is connected.
		/// </summary>
		public static event EventHandler<VirtualDesktop> RemoteDesktopConnected;

		internal static class EventRaiser
		{
			internal static void Publish(VirtualDesktopProvider provider, VirtualDesktopCallbackDto dto, object sender, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, long sequence)
			{
				switch (dto.Kind)
				{
					case VirtualDesktopCallbackKind.Created:
						{
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.Created, sequence, out var desktop)) break;
							_desktopCaches = null;
							History.AddIfInitialized(desktop);
							Invoke(Created, sender, desktop, pipeline, exceptions, VirtualDesktopProviderEventKind.Created, desktop.Id, sequence);
							break;
						}
					case VirtualDesktopCallbackKind.DestroyBegin:
					case VirtualDesktopCallbackKind.DestroyFailed:
					case VirtualDesktopCallbackKind.Destroyed:
						{
							var eventKind = VirtualDesktopEventPipeline.ToPublicKind(dto.Kind);
							var managedOnly = dto.Kind == VirtualDesktopCallbackKind.Destroyed;
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, managedOnly, eventKind, sequence, out var desktop)) break;
							if (!TryResolve(provider, pipeline, dto.RelatedDesktopId.Value, false, eventKind, sequence, out var fallback)) break;
							if (dto.Kind == VirtualDesktopCallbackKind.Destroyed) { _desktopCaches = null; History.RemoveIfInitialized(desktop); }
							var args = new VirtualDesktopDestroyEventArgs(desktop, fallback);
							if (dto.Kind == VirtualDesktopCallbackKind.DestroyBegin) Invoke(DestroyBegin, sender, args, pipeline, exceptions, VirtualDesktopProviderEventKind.DestroyBegin, desktop.Id, sequence);
							else if (dto.Kind == VirtualDesktopCallbackKind.DestroyFailed) Invoke(DestroyFailed, sender, args, pipeline, exceptions, VirtualDesktopProviderEventKind.DestroyFailed, desktop.Id, sequence);
							else
							{
								Invoke(Destroyed, sender, args, pipeline, exceptions, VirtualDesktopProviderEventKind.Destroyed, desktop.Id, sequence);
								provider.RemoveDesktop(desktop.Id);
							}
							break;
						}
					case VirtualDesktopCallbackKind.Moved:
						{
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.Moved, sequence, out var desktop)) break;
							_desktopCaches = null;
							Invoke(Moved, sender, new VirtualDesktopMovedEventArgs(desktop, dto.OldIndex.Value, dto.NewIndex.Value), pipeline, exceptions, VirtualDesktopProviderEventKind.Moved, desktop.Id, sequence);
							break;
						}
					case VirtualDesktopCallbackKind.ApplicationViewChanged:
						Invoke(ApplicationViewChanged, sender, EventArgs.Empty, pipeline, exceptions, VirtualDesktopProviderEventKind.ApplicationViewChanged, null, sequence);
						break;
					case VirtualDesktopCallbackKind.CurrentChanged:
						{
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.CurrentChanged, sequence, out var oldDesktop)) break;
							if (!TryResolve(provider, pipeline, dto.RelatedDesktopId.Value, false, VirtualDesktopProviderEventKind.CurrentChanged, sequence, out var newDesktop)) break;
							History.SetPreviousIfInitialized(oldDesktop);
							Invoke(CurrentChanged, sender, new VirtualDesktopChangedEventArgs(oldDesktop, newDesktop), pipeline, exceptions, VirtualDesktopProviderEventKind.CurrentChanged, newDesktop.Id, sequence);
							break;
						}
					case VirtualDesktopCallbackKind.Renamed:
						if (pipeline.Mode == VirtualDesktopEventMode.LegacyInline
							&& TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.Renamed, sequence, out var renamedDesktop))
							pipeline.ApplyLegacyNameNotification(renamedDesktop, dto.Value, sender, exceptions, sequence);
						break;
					case VirtualDesktopCallbackKind.WallpaperChanged:
						if (pipeline.Mode == VirtualDesktopEventMode.LegacyInline
							&& TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.WallpaperChanged, sequence, out var wallpaperDesktop))
							pipeline.ApplyLegacyWallpaperNotification(wallpaperDesktop, dto.Value, sender, exceptions, sequence);
						break;
					case VirtualDesktopCallbackKind.DesktopSwitched:
						{
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.DesktopSwitched, sequence, out var desktop)) break;
							Invoke(DesktopSwitched, sender, desktop, pipeline, exceptions, VirtualDesktopProviderEventKind.DesktopSwitched, desktop.Id, sequence);
							break;
						}
					case VirtualDesktopCallbackKind.RemoteDesktopConnected:
						{
							if (!TryResolve(provider, pipeline, dto.DesktopId.Value, false, VirtualDesktopProviderEventKind.RemoteDesktopConnected, sequence, out var desktop)) break;
							Invoke(RemoteDesktopConnected, sender, desktop, pipeline, exceptions, VirtualDesktopProviderEventKind.RemoteDesktopConnected, desktop.Id, sequence);
							break;
						}
				}
			}

			private static bool TryResolve(VirtualDesktopProvider provider, VirtualDesktopEventPipeline pipeline, Guid id, bool managedOnly, VirtualDesktopProviderEventKind kind, long sequence, out VirtualDesktop desktop)
			{
				if (provider.TryResolveDesktop(id, managedOnly, out desktop, out var error)) return true;
				pipeline.ReportFault(VirtualDesktopProviderFaultPhase.EventDispatch, kind, id, error ?? new InvalidOperationException("A callback desktop could not be resolved."), sequence);
				return false;
			}

			internal static void RaiseRenamed(object sender, VirtualDesktop desktop, string oldName, string newName, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, long sequence)
				=> Invoke(Renamed, sender, new VirtualDesktopRenamedEventArgs(desktop, oldName, newName), pipeline, exceptions, VirtualDesktopProviderEventKind.Renamed, desktop.Id, sequence);

			internal static void RaiseWallpaperChanged(object sender, VirtualDesktop desktop, string oldPath, string newPath, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, long sequence)
				=> Invoke(WallpaperChanged, sender, new VirtualDesktopWallpaperChangedEventArgs(desktop, oldPath, newPath), pipeline, exceptions, VirtualDesktopProviderEventKind.WallpaperChanged, desktop.Id, sequence);

			private static void Invoke<T>(EventHandler<T> handlers, object sender, T args, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, VirtualDesktopProviderEventKind kind, Guid? desktopId, long sequence)
			{
				if (handlers == null) return;
				foreach (EventHandler<T> handler in handlers.GetInvocationList())
					pipeline.InvokeSubscriber(handler, () => handler(sender, args), VirtualDesktopProviderFaultPhase.EventDispatch, kind, desktopId, sequence, exceptions);
			}

			private static void Invoke(EventHandler handlers, object sender, EventArgs args, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, VirtualDesktopProviderEventKind kind, Guid? desktopId, long sequence)
			{
				if (handlers == null) return;
				foreach (EventHandler handler in handlers.GetInvocationList())
					pipeline.InvokeSubscriber(handler, () => handler(sender, args), VirtualDesktopProviderFaultPhase.EventDispatch, kind, desktopId, sequence, exceptions);
			}
		}
	}
}
