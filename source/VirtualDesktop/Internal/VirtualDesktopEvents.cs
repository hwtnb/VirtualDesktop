using System;
using System.Reflection;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop;

namespace WindowsDesktop.Internal
{
	internal enum VirtualDesktopEventMode { LegacyInline, StrongScheduled }
	internal enum VirtualDesktopCallbackKind { Created, DestroyBegin, DestroyFailed, Destroyed, Moved, ApplicationViewChanged, CurrentChanged, Renamed, WallpaperChanged, DesktopSwitched, RemoteDesktopConnected }

	internal sealed class VirtualDesktopCallbackDto
	{
		internal VirtualDesktopCallbackDto(VirtualDesktopCallbackKind kind, Guid? desktopId = null, Guid? relatedDesktopId = null, int? oldIndex = null, int? newIndex = null, string value = null)
		{
			this.Kind = kind;
			this.DesktopId = desktopId;
			this.RelatedDesktopId = relatedDesktopId;
			this.OldIndex = oldIndex;
			this.NewIndex = newIndex;
			this.Value = value;
		}

		internal VirtualDesktopCallbackKind Kind { get; }
		internal Guid? DesktopId { get; }
		internal Guid? RelatedDesktopId { get; }
		internal int? OldIndex { get; }
		internal int? NewIndex { get; }
		internal string Value { get; }
	}

	internal sealed class VirtualDesktopCallbackMaterializer
	{
		private readonly Type _desktopType;

		internal VirtualDesktopCallbackMaterializer(ComInterfaceAssembly assembly)
			: this(VirtualDesktopSnapshotInterfaceResolver.Resolve(new ComAssemblyVirtualDesktopSnapshotInterfaceCatalog(assembly)).InterfaceType)
		{
		}

		internal VirtualDesktopCallbackMaterializer(Type desktopType)
		{
			this._desktopType = desktopType ?? throw new ArgumentNullException(nameof(desktopType));
		}

		internal VirtualDesktopCallbackDto One(VirtualDesktopCallbackKind kind, object desktop)
			=> new VirtualDesktopCallbackDto(kind, this.ReadId(desktop));

		internal VirtualDesktopCallbackDto Two(VirtualDesktopCallbackKind kind, object desktop, object relatedDesktop)
			=> new VirtualDesktopCallbackDto(kind, this.ReadId(desktop), this.ReadId(relatedDesktop));

		internal VirtualDesktopCallbackDto Move(object desktop, int oldIndex, int newIndex)
			=> new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Moved, this.ReadId(desktop), oldIndex: oldIndex, newIndex: newIndex);

		internal VirtualDesktopCallbackDto Property(VirtualDesktopCallbackKind kind, object desktop, string value)
			=> new VirtualDesktopCallbackDto(kind, this.ReadId(desktop), value: value ?? string.Empty);

		internal VirtualDesktopCallbackDto ApplicationViewChanged()
			=> new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged);

		private Guid ReadId(object desktop)
		{
			if (desktop == null) throw new ArgumentNullException(nameof(desktop));
			var method = this._desktopType.GetMethod("GetID");
			if (method == null) throw new NotSupportedException("GetID is not resolved.");
			try
			{
				var id = (Guid)method.Invoke(desktop, null);
				if (id == Guid.Empty) throw new InvalidOperationException("A callback desktop ID was empty.");
				return id;
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				throw ex.InnerException;
			}
		}
	}

	internal sealed class VirtualDesktopMirrorTransition
	{
		private readonly Func<string> _read;
		private readonly Action<string> _commit;

		internal VirtualDesktopMirrorTransition(Func<string> read, Action<string> commit)
		{
			this._read = read ?? throw new ArgumentNullException(nameof(read));
			this._commit = commit ?? throw new ArgumentNullException(nameof(commit));
		}

		internal bool Apply(string newValue, Action<string, string> changing, Action<string, string> changed, Action<string, string> published)
		{
			var oldValue = this._read();
			if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return false;
			changing?.Invoke(oldValue, newValue);
			this._commit(newValue);
			changed?.Invoke(oldValue, newValue);
			published?.Invoke(oldValue, newValue);
			return true;
		}
	}

	internal sealed class VirtualDesktopEventPipeline
	{
		private readonly VirtualDesktopProvider _provider;
		private readonly IEventScheduler _scheduler;
		private readonly EventIngress<VirtualDesktopCallbackDto> _ingress;
		private readonly object _acceptGate = new object();
		private readonly object _callbackGate = new object();
		private bool _accepting = true;
		private bool _shutdownStarted;
		private bool _shutdownSchedulingComplete;
		private int _legacyPublicationsInFlight;
		private Action _shutdownCompletion;
		private int _callbackThreadId;
		private int _callbackDepth;

		internal VirtualDesktopEventPipeline(VirtualDesktopProvider provider, IEventScheduler scheduler)
		{
			this._provider = provider ?? throw new ArgumentNullException(nameof(provider));
			this._scheduler = scheduler;
			this.Mode = scheduler == null ? VirtualDesktopEventMode.LegacyInline : VirtualDesktopEventMode.StrongScheduled;
			if (scheduler != null)
			{
				this._ingress = new EventIngress<VirtualDesktopCallbackDto>(scheduler, item => this.Publish(item.Value, item.Sequence, false, this._provider), (exception, sequence) => this.ReportFault(VirtualDesktopProviderFaultPhase.Scheduling, VirtualDesktopProviderEventKind.Unknown, null, exception, sequence));
			}
		}

		internal VirtualDesktopEventMode Mode { get; }
		internal int PendingCount => this._ingress?.PendingCount ?? 0;
		internal int PostCount => this._ingress?.PostCount ?? 0;
		internal EventPumpState PumpState => this._ingress?.State ?? EventPumpState.Ready;
		internal bool CheckAccess() => this._scheduler == null || this._scheduler.CheckAccess();

		internal EventEnqueueResult Accept(VirtualDesktopCallbackDto dto, object legacySender = null)
		{
			bool accepted;
			lock (this._acceptGate)
			{
				accepted = this._accepting;
				if (accepted && this.Mode == VirtualDesktopEventMode.LegacyInline)
				{
					this._legacyPublicationsInFlight++;
				}
			}
			if (!accepted)
			{
				this.ReportFault(VirtualDesktopProviderFaultPhase.Shutdown, ToPublicKind(dto.Kind), dto.DesktopId, new InvalidOperationException("The event pipeline is shut down."), 0);
				return new EventEnqueueResult(EventEnqueueStatus.Rejected, null, EventEnqueueRejectionReason.Shutdown);
			}
			if (this.Mode == VirtualDesktopEventMode.LegacyInline)
			{
				try
				{
					this.Publish(dto, 0, true, legacySender ?? this._provider);
					return new EventEnqueueResult(EventEnqueueStatus.Accepted, 0);
				}
				finally { this.ReleaseLegacyPublication(); }
			}
			var result = this._ingress.Enqueue(dto);
			if (result.RejectionReason == EventEnqueueRejectionReason.Shutdown)
				this.ReportFault(VirtualDesktopProviderFaultPhase.Shutdown, ToPublicKind(dto.Kind), dto.DesktopId, new InvalidOperationException("The event pipeline is shut down."), 0);
			return result;
		}

		internal void ReportMaterializationFailure(VirtualDesktopCallbackKind kind, Exception exception)
			=> this.ReportFault(VirtualDesktopProviderFaultPhase.CallbackMaterialization, ToPublicKind(kind), null, exception, 0);

		internal void Shutdown(Action completion = null)
		{
			bool shutdownIngress = false;
			Action ready;
			lock (this._acceptGate)
			{
				this._accepting = false;
				this._shutdownCompletion += completion;
				if (!this._shutdownStarted)
				{
					this._shutdownStarted = true;
					shutdownIngress = true;
				}
			}
			if (shutdownIngress) this._ingress?.Shutdown();
			lock (this._acceptGate)
			{
				if (shutdownIngress) this._shutdownSchedulingComplete = true;
				ready = this.TakeShutdownCompletionIfReady();
			}
			this.InvokeShutdownCompletion(ready);
		}

		internal void ValidateSetterAccess()
		{
			if (this.Mode != VirtualDesktopEventMode.StrongScheduled) return;
			if (!this._scheduler.CheckAccess()) throw new InvalidOperationException("A strong-mode virtual desktop setter must run on the event owner context.");
			if (this._provider.IsReportingEventFault) throw new InvalidOperationException("A virtual desktop setter cannot be called reentrantly from a fault callback.");
			lock (this._callbackGate)
			{
				if (this._callbackDepth > 0 && this._callbackThreadId == Environment.CurrentManagedThreadId)
					throw new InvalidOperationException("A virtual desktop setter cannot be called reentrantly from a public callback.");
			}
		}

		internal void ExecuteSetter(Action comSetter, Action commit)
		{
			if (comSetter == null) throw new ArgumentNullException(nameof(comSetter));
			if (commit == null) throw new ArgumentNullException(nameof(commit));
			this.ValidateSetterAccess();
			comSetter();
			commit();
		}

		internal void ApplyLocalName(VirtualDesktop desktop, string value)
			=> this.ApplyLocalProperty(desktop, value, VirtualDesktopProviderEventKind.Renamed, (oldValue, newValue, exceptions, sequence) => VirtualDesktop.EventRaiser.RaiseRenamed(this._provider, desktop, oldValue, newValue, this, exceptions, sequence));

		internal void ApplyLocalWallpaper(VirtualDesktop desktop, string value)
			=> this.ApplyLocalProperty(desktop, value, VirtualDesktopProviderEventKind.WallpaperChanged, (oldValue, newValue, exceptions, sequence) => VirtualDesktop.EventRaiser.RaiseWallpaperChanged(this._provider, desktop, oldValue, newValue, this, exceptions, sequence));

		internal void ApplyLegacyNameNotification(VirtualDesktop desktop, string value, object sender, ExceptionCollector exceptions, long sequence)
			=> this.ApplyLegacyProperty(desktop, value, VirtualDesktopProviderEventKind.Renamed, (oldValue, newValue) => VirtualDesktop.EventRaiser.RaiseRenamed(sender, desktop, oldValue, newValue, this, exceptions, sequence), exceptions, sequence);

		internal void ApplyLegacyWallpaperNotification(VirtualDesktop desktop, string value, object sender, ExceptionCollector exceptions, long sequence)
			=> this.ApplyLegacyProperty(desktop, value, VirtualDesktopProviderEventKind.WallpaperChanged, (oldValue, newValue) => VirtualDesktop.EventRaiser.RaiseWallpaperChanged(sender, desktop, oldValue, newValue, this, exceptions, sequence), exceptions, sequence);

		internal void InvokeSubscriber(Delegate subscriber, Action invoke, VirtualDesktopProviderFaultPhase phase, VirtualDesktopProviderEventKind kind, Guid? desktopId, long sequence, ExceptionCollector exceptions)
		{
			this.EnterCallback();
			try
			{
				try { invoke(); }
				catch (Exception ex)
				{
					if (this.Mode == VirtualDesktopEventMode.StrongScheduled) this.ReportFault(phase, kind, desktopId, ex, sequence);
					else exceptions.Add(ex);
				}
			}
			finally { this.ExitCallback(); }
		}

		internal void ReportFault(VirtualDesktopProviderFaultPhase phase, VirtualDesktopProviderEventKind kind, Guid? desktopId, Exception exception, long sequence)
			=> this._provider.ReportEventFault(new VirtualDesktopProviderFault(phase, kind, desktopId, exception.GetType().FullName, GetNativeErrorCode(exception), sequence));

		private void Publish(VirtualDesktopCallbackDto dto, long sequence, bool rethrow, object sender)
		{
			var exceptions = new ExceptionCollector();
			try { VirtualDesktop.EventRaiser.Publish(this._provider, dto, sender, this, exceptions, sequence); }
			catch (Exception ex) { this.ReportFault(VirtualDesktopProviderFaultPhase.EventDispatch, ToPublicKind(dto.Kind), dto.DesktopId, ex, sequence); if (rethrow) exceptions.Add(ex); }
			if (rethrow) exceptions.ThrowFirst();
		}

		private void ApplyLocalProperty(VirtualDesktop desktop, string value, VirtualDesktopProviderEventKind kind, Action<string, string, ExceptionCollector, long> publish)
		{
			var exceptions = new ExceptionCollector();
			var publicEvent = this.Mode == VirtualDesktopEventMode.StrongScheduled
				? new Action<string, string>((oldValue, newValue) => publish(oldValue, newValue, exceptions, 0))
				: null;
			this.ApplyPropertyCore(desktop, value, kind, publicEvent, exceptions, 0);
			if (this.Mode == VirtualDesktopEventMode.LegacyInline) exceptions.ThrowFirst();
		}

		private void ApplyLegacyProperty(VirtualDesktop desktop, string value, VirtualDesktopProviderEventKind kind, Action<string, string> publish, ExceptionCollector exceptions, long sequence)
		{
			var oldValue = kind == VirtualDesktopProviderEventKind.Renamed ? desktop.Name : desktop.WallpaperPath;
			this.ApplyPropertyCore(desktop, value, kind, null, exceptions, sequence);
			publish(oldValue, value);
		}

		private void ApplyPropertyCore(VirtualDesktop desktop, string value, VirtualDesktopProviderEventKind kind, Action<string, string> publish, ExceptionCollector exceptions, long sequence)
		{
			if (kind == VirtualDesktopProviderEventKind.Renamed)
			{
				var transition = new VirtualDesktopMirrorTransition(() => desktop.Name, desktop.CommitNameMirror);
				transition.Apply(value,
					(oldValue, newValue) => desktop.RaisePropertyChangingSafely(nameof(VirtualDesktop.Name), this, exceptions, kind, sequence),
					(oldValue, newValue) => desktop.RaisePropertyChangedSafely(nameof(VirtualDesktop.Name), this, exceptions, kind, sequence),
					publish);
			}
			else
			{
				var transition = new VirtualDesktopMirrorTransition(() => desktop.WallpaperPath, desktop.CommitWallpaperMirror);
				transition.Apply(value,
					(oldValue, newValue) => desktop.RaisePropertyChangingSafely(nameof(VirtualDesktop.WallpaperPath), this, exceptions, kind, sequence),
					(oldValue, newValue) => desktop.RaisePropertyChangedSafely(nameof(VirtualDesktop.WallpaperPath), this, exceptions, kind, sequence),
					publish);
			}
		}

		private void EnterCallback()
		{
			lock (this._callbackGate) { this._callbackThreadId = Environment.CurrentManagedThreadId; this._callbackDepth++; }
		}

		private void ExitCallback()
		{
			lock (this._callbackGate) { this._callbackDepth--; if (this._callbackDepth == 0) this._callbackThreadId = 0; }
		}

		private void ReleaseLegacyPublication()
		{
			Action ready;
			lock (this._acceptGate)
			{
				this._legacyPublicationsInFlight--;
				ready = this.TakeShutdownCompletionIfReady();
			}
			this.InvokeShutdownCompletion(ready);
		}

		private Action TakeShutdownCompletionIfReady()
		{
			if (!this._shutdownSchedulingComplete || this._legacyPublicationsInFlight != 0) return null;
			var ready = this._shutdownCompletion;
			this._shutdownCompletion = null;
			return ready;
		}

		private void InvokeShutdownCompletion(Action completion)
		{
			if (completion == null) return;
			try { completion(); }
			catch (Exception ex) { this.ReportFault(VirtualDesktopProviderFaultPhase.Shutdown, VirtualDesktopProviderEventKind.Unknown, null, ex, 0); }
		}

		private static int? GetNativeErrorCode(Exception exception)
		{
			var current = exception;
			while (current is TargetInvocationException && current.InnerException != null) current = current.InnerException;
			return current is COMException ? (int?)current.HResult : null;
		}

		internal static VirtualDesktopProviderEventKind ToPublicKind(VirtualDesktopCallbackKind kind)
		{
			switch (kind)
			{
				case VirtualDesktopCallbackKind.Created: return VirtualDesktopProviderEventKind.Created;
				case VirtualDesktopCallbackKind.DestroyBegin: return VirtualDesktopProviderEventKind.DestroyBegin;
				case VirtualDesktopCallbackKind.DestroyFailed: return VirtualDesktopProviderEventKind.DestroyFailed;
				case VirtualDesktopCallbackKind.Destroyed: return VirtualDesktopProviderEventKind.Destroyed;
				case VirtualDesktopCallbackKind.Moved: return VirtualDesktopProviderEventKind.Moved;
				case VirtualDesktopCallbackKind.ApplicationViewChanged: return VirtualDesktopProviderEventKind.ApplicationViewChanged;
				case VirtualDesktopCallbackKind.CurrentChanged: return VirtualDesktopProviderEventKind.CurrentChanged;
				case VirtualDesktopCallbackKind.Renamed: return VirtualDesktopProviderEventKind.Renamed;
				case VirtualDesktopCallbackKind.WallpaperChanged: return VirtualDesktopProviderEventKind.WallpaperChanged;
				case VirtualDesktopCallbackKind.DesktopSwitched: return VirtualDesktopProviderEventKind.DesktopSwitched;
				case VirtualDesktopCallbackKind.RemoteDesktopConnected: return VirtualDesktopProviderEventKind.RemoteDesktopConnected;
				default: return VirtualDesktopProviderEventKind.Unknown;
			}
		}
	}

	internal sealed class ExceptionCollector
	{
		private Exception _first;
		internal void Add(Exception exception) { if (this._first == null) this._first = exception; }
		internal void ThrowFirst() { if (this._first != null) throw this._first; }
	}
}
