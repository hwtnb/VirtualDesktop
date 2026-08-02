using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDesktop.Interop;
using WindowsDesktop.Internal;
using WindowsDesktop.Properties;

namespace WindowsDesktop
{
	public class VirtualDesktopProvider : IDisposable
	{
		#region Default instance

		private static readonly Lazy<VirtualDesktopProvider> _default = new Lazy<VirtualDesktopProvider>(() => new VirtualDesktopProvider());

		public static VirtualDesktopProvider Default => _default.Value;

		#endregion

		private Task _initializationTask;
		private ComObjects _comObjects;
		private readonly object _configurationGate = new object();
		private IEventScheduler _eventScheduler;
		private object _eventSchedulerIdentity;
		private VirtualDesktopEventPipeline _eventPipeline;
		private readonly IVirtualDesktopProviderRuntime _runtime;
		private int _disposeSignaled;
		[ThreadStatic]
		private static HashSet<VirtualDesktopProvider> _faultingProviders;

		public event EventHandler<VirtualDesktopProviderFault> EventDispatchFaulted;

		public VirtualDesktopProvider()
		{
			this._runtime = new ComVirtualDesktopProviderRuntime(this);
		}

		internal VirtualDesktopProvider(IVirtualDesktopProviderRuntime runtime)
		{
			this._runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		}

		public string ComInterfaceAssemblyPath { get; set; }

		public bool AutoRestart { get; set; } = true;

		internal ComObjects ComObjects
		{
			get
			{
				while (!_comObjects.IsAvailable)
				{
					Thread.Sleep(1);
				}
				return _comObjects;
			}
			private set => _comObjects = value;
		}

		public Task Initialize()
			=> this.Initialize(TaskScheduler.FromCurrentSynchronizationContext());

		public Task Initialize(TaskScheduler scheduler)
		{
			lock (this._configurationGate)
			{
				if (this._initializationTask != null) return this._initializationTask;
				this._eventPipeline = new VirtualDesktopEventPipeline(this, this._eventScheduler);
				this._initializationTask = Task.Run(() => Core());

				if (this.AutoRestart && scheduler != null)
				{
					this._initializationTask.ContinueWith(
						_ => this.ComObjects.Listen(),
						CancellationToken.None,
						TaskContinuationOptions.OnlyOnRanToCompletion,
						scheduler);
				}
				return this._initializationTask;
			}

			void Core()
			{
				var assemblyProvider = new ComInterfaceAssemblyProvider(this.ComInterfaceAssemblyPath);
				var assembly = new ComInterfaceAssembly(assemblyProvider.GetAssembly());

				this.ComObjects = new ComObjects(assembly, this, this._eventPipeline);
			}
		}

		internal void EnableEventScheduling(IEventScheduler scheduler, object identity)
		{
			if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));
			if (identity == null) throw new ArgumentNullException(nameof(identity));
			lock (this._configurationGate)
			{
				if (this._initializationTask != null) throw new InvalidOperationException("Event scheduling must be enabled before provider initialization.");
				if (this._eventScheduler != null)
				{
					if (ReferenceEquals(this._eventSchedulerIdentity, identity)) return;
					throw new InvalidOperationException("The provider event scheduler cannot be rebound.");
				}
				this._eventScheduler = scheduler;
				this._eventSchedulerIdentity = identity;
			}
		}

		internal VirtualDesktopEventPipeline EventPipeline
		{
			get
			{
				lock (this._configurationGate)
				{
					if (this._eventPipeline == null) this._eventPipeline = new VirtualDesktopEventPipeline(this, this._eventScheduler);
					return this._eventPipeline;
				}
			}
		}

		internal bool TryResolveDesktop(Guid id, bool managedOnly, out VirtualDesktop desktop, out Exception error)
		{
			try
			{
				error = null;
				return this._runtime.TryResolveDesktop(id, managedOnly, out desktop);
			}
			catch (Exception ex)
			{
				desktop = null;
				error = ex;
				return false;
			}
		}

		internal void RegisterDesktop(VirtualDesktop desktop) => this._runtime.RegisterDesktop(desktop);

		internal void RemoveDesktop(Guid id) => this._runtime.RemoveDesktop(id);

		internal void SetDesktopName(VirtualDesktop desktop, string value)
		{
			var pipeline = this.EventPipeline;
			pipeline.ExecuteSetter(
				() =>
				{
					if (ProductInfo.OSBuild < 20231 && desktop.ComVersion < 2) throw new PlatformNotSupportedException("This Windows 10 version is not supported.");
					this._runtime.SetDesktopName(desktop, value);
				},
				() => pipeline.ApplyLocalName(desktop, value));
		}

		internal void SetDesktopWallpaper(VirtualDesktop desktop, string value)
		{
			var pipeline = this.EventPipeline;
			pipeline.ExecuteSetter(
				() =>
				{
					if (ProductInfo.OSBuild < 21313) throw new PlatformNotSupportedException("This Windows 10 version is not supported.");
					this._runtime.SetDesktopWallpaper(desktop, value);
				},
				() => pipeline.ApplyLocalWallpaper(desktop, value));
		}

		internal void ReportEventFault(VirtualDesktopProviderFault fault)
		{
			if (_faultingProviders == null) _faultingProviders = new HashSet<VirtualDesktopProvider>();
			if (!_faultingProviders.Add(this)) return;
			try
			{
				var handlers = this.EventDispatchFaulted;
				if (handlers == null) return;
				foreach (EventHandler<VirtualDesktopProviderFault> handler in handlers.GetInvocationList())
				{
					try { handler(this, fault); }
					catch { }
				}
			}
			finally { _faultingProviders.Remove(this); }
		}

		internal bool IsReportingEventFault => _faultingProviders != null && _faultingProviders.Contains(this);

		public bool TryDeleteAssembly()
		{
			var assemblyProvider = new ComInterfaceAssemblyProvider(this.ComInterfaceAssemblyPath);
			return assemblyProvider.TryDeleteAssembly();
		}

		internal VirtualDesktopSnapshotBatch CaptureSnapshot()
			=> this.ComObjects.VirtualDesktopManagerInternal.CaptureSnapshot();

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this._disposeSignaled, 1) != 0) return;
			var pipeline = this._eventPipeline;
			if (pipeline == null) this._comObjects?.Dispose();
			else pipeline.Shutdown(() => this._comObjects?.Dispose());
		}
	}

	partial class VirtualDesktop
	{
		public static VirtualDesktopProvider Provider { get; set; }

		internal static VirtualDesktopProvider ProviderInternal
			=> Provider ?? VirtualDesktopProvider.Default;
	}
}
