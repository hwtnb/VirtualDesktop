using System;
using System.Collections.Generic;
using System.Threading;
using WindowsDesktop.Interop;

namespace WindowsDesktop.Internal
{
	internal enum VirtualDesktopPropertyKind { Name, WallpaperPath }
	internal enum ReconciliationIngressDisposition { None, Reconcile, CurrentOnly, Acknowledged, Duplicate, OldEpoch }

	internal interface IVirtualDesktopSnapshotCapture
	{
		VirtualDesktopSnapshotBatch Capture();
	}

	internal sealed class ProviderVirtualDesktopSnapshotCapture : IVirtualDesktopSnapshotCapture
	{
		private readonly VirtualDesktopProvider _provider;
		internal ProviderVirtualDesktopSnapshotCapture(VirtualDesktopProvider provider) => this._provider = provider;
		public VirtualDesktopSnapshotBatch Capture() => this._provider.CaptureSnapshotCore();
	}

	internal interface IReconciliationDelayScheduler
	{
		IDisposable Schedule(TimeSpan delay, Action callback);
	}

	internal sealed class TimerReconciliationDelayScheduler : IReconciliationDelayScheduler
	{
		public IDisposable Schedule(TimeSpan delay, Action callback)
		{
			if (callback == null) throw new ArgumentNullException(nameof(callback));
			var registration = new TimerDelayRegistration(callback);
			registration.Start(delay);
			return registration;
		}

		private sealed class TimerDelayRegistration : IDisposable
		{
			private readonly object _gate = new object();
			private readonly Action _callback;
			private Timer _timer;
			private bool _disposed;

			internal TimerDelayRegistration(Action callback) => this._callback = callback;

			internal void Start(TimeSpan delay)
			{
				var timer = new Timer(_ => this.Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
				lock (this._gate)
				{
					if (this._disposed) { timer.Dispose(); return; }
					this._timer = timer;
					timer.Change(delay, Timeout.InfiniteTimeSpan);
				}
			}

			private void Fire()
			{
				Timer timer;
				lock (this._gate)
				{
					if (this._disposed) return;
					this._disposed = true;
					timer = this._timer;
					this._timer = null;
				}
				timer?.Dispose();
				this._callback();
			}

			public void Dispose()
			{
				Timer timer;
				lock (this._gate)
				{
					if (this._disposed) return;
					this._disposed = true;
					timer = this._timer;
					this._timer = null;
				}
				timer?.Dispose();
			}
		}
	}

	internal sealed class ReconciliationRetryPolicy
	{
		private readonly TimeSpan[] _delays;
		internal ReconciliationRetryPolicy(params TimeSpan[] delays)
		{
			if (delays == null) throw new ArgumentNullException(nameof(delays));
			this._delays = (TimeSpan[])delays.Clone();
			for (var i = 0; i < this._delays.Length; i++) if (this._delays[i] < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delays));
		}
		internal int MaximumRetries => this._delays.Length;
		internal TimeSpan GetDelay(int zeroBasedRetry) => this._delays[Math.Min(zeroBasedRetry, this._delays.Length - 1)];
		internal static ReconciliationRetryPolicy Default { get; } = new ReconciliationRetryPolicy(
			TimeSpan.FromMilliseconds(25),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(500),
			TimeSpan.FromSeconds(2));
	}

	internal struct VirtualDesktopPropertyKey : IEquatable<VirtualDesktopPropertyKey>
	{
		internal VirtualDesktopPropertyKey(Guid desktopId, VirtualDesktopPropertyKind property) { this.DesktopId = desktopId; this.Property = property; }
		internal Guid DesktopId { get; }
		internal VirtualDesktopPropertyKind Property { get; }
		public bool Equals(VirtualDesktopPropertyKey other) => this.DesktopId == other.DesktopId && this.Property == other.Property;
		public override bool Equals(object obj) => obj is VirtualDesktopPropertyKey other && this.Equals(other);
		public override int GetHashCode() => (this.DesktopId.GetHashCode() * 397) ^ (int)this.Property;
	}

	internal sealed class PendingLocalWrite
	{
		internal PendingLocalWrite(long providerEpoch, Guid desktopId, VirtualDesktopPropertyKind property, long generation, string targetValue, long targetIngressRevision)
		{
			this.ProviderEpoch = providerEpoch;
			this.DesktopId = desktopId;
			this.Property = property;
			this.Generation = generation;
			this.TargetValue = targetValue;
			this.TargetIngressRevision = targetIngressRevision;
		}
		internal long ProviderEpoch { get; }
		internal Guid DesktopId { get; }
		internal VirtualDesktopPropertyKind Property { get; }
		internal long Generation { get; }
		internal string TargetValue { get; }
		internal long TargetIngressRevision { get; }
		internal bool AcknowledgementObserved { get; set; }
	}

	internal sealed class ReconciliationRetryRegistration : IDisposable
	{
		private readonly object _gate = new object();
		private readonly Action<ReconciliationRetryRegistration> _callback;
		private IDisposable _inner;
		private bool _fired;
		private bool _disposed;

		internal ReconciliationRetryRegistration(Action<ReconciliationRetryRegistration> callback)
		{
			this._callback = callback ?? throw new ArgumentNullException(nameof(callback));
		}

		internal void Fire()
		{
			lock (this._gate)
			{
				if (this._disposed || this._fired) return;
				this._fired = true;
			}
			this._callback(this);
		}

		internal void Attach(IDisposable registration)
		{
			if (registration == null) throw new ArgumentNullException(nameof(registration));
			bool dispose;
			lock (this._gate)
			{
				dispose = this._disposed || this._fired;
				if (!dispose) this._inner = registration;
			}
			if (dispose) registration.Dispose();
		}

		public void Dispose()
		{
			IDisposable inner;
			lock (this._gate)
			{
				if (this._disposed) return;
				this._disposed = true;
				inner = this._inner;
				this._inner = null;
			}
			inner?.Dispose();
		}
	}

	internal sealed class EmptyPropertyCandidate
	{
		internal EmptyPropertyCandidate(long providerEpoch, long firstSnapshotRevision)
		{
			this.ProviderEpoch = providerEpoch;
			this.FirstSnapshotRevision = firstSnapshotRevision;
			this.SuccessfulEmptyReads = 1;
		}
		internal long ProviderEpoch { get; }
		internal long FirstSnapshotRevision { get; }
		internal int SuccessfulEmptyReads { get; set; }
	}

	internal sealed class ManagedDesktopPropertyState
	{
		internal string Value;
		internal VirtualDesktopReadStatus Status;
		internal bool HasValue;
	}

	internal sealed class ManagedDesktopState
	{
		internal ManagedDesktopState(Guid id) { this.Id = id; }
		internal Guid Id { get; }
		internal int OrderIndex;
		internal ManagedDesktopPropertyState Name { get; } = new ManagedDesktopPropertyState();
		internal ManagedDesktopPropertyState Wallpaper { get; } = new ManagedDesktopPropertyState();
	}

	internal sealed class ReconciliationWaiter
	{
		private readonly object _cancellationGate = new object();
		private CancellationTokenRegistration _cancellation;
		private bool _cancellationInstalled;
		private bool _cancellationDisposeRequested;
		private bool _cancellationDisposed;

		internal ReconciliationWaiter(long epoch, long targetRevision, CancellationToken cancellationToken)
		{
			this.Epoch = epoch;
			this.TargetRevision = targetRevision;
			this.Completion = new System.Threading.Tasks.TaskCompletionSource<VirtualDesktopReconciliationResult>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
			if (cancellationToken.CanBeCanceled)
			{
				var registration = cancellationToken.Register(this.OnCancelled);
				bool dispose;
				lock (this._cancellationGate)
				{
					this._cancellation = registration;
					this._cancellationInstalled = true;
					dispose = this._cancellationDisposeRequested;
				}
				if (dispose) this.QueueCancellationDisposal();
			}
		}
		internal long Epoch { get; }
		internal long TargetRevision { get; }
		internal System.Threading.Tasks.TaskCompletionSource<VirtualDesktopReconciliationResult> Completion { get; }
		internal bool CancellationDisposed { get { lock (this._cancellationGate) return this._cancellationDisposed; } }
		internal void Complete(VirtualDesktopReconciliationResult result)
		{
			this.DisposeCancellation();
			this.Completion.TrySetResult(result);
		}

		internal void DisposeCancellation()
		{
			CancellationTokenRegistration registration;
			lock (this._cancellationGate)
			{
				if (this._cancellationDisposed) return;
				if (!this._cancellationInstalled) { this._cancellationDisposeRequested = true; return; }
				this._cancellationDisposed = true;
				registration = this._cancellation;
			}
			registration.Dispose();
		}

		private void OnCancelled()
		{
			this.Completion.TrySetResult(VirtualDesktopReconciliationResult.Cancelled());
			lock (this._cancellationGate) this._cancellationDisposeRequested = true;
			this.QueueCancellationDisposal();
		}

		private void QueueCancellationDisposal()
			=> ThreadPool.QueueUserWorkItem(_ => this.DisposeCancellation());
	}

	internal sealed class PropertyMirrorTransition
	{
		internal PropertyMirrorTransition(Guid desktopId, VirtualDesktopPropertyKind property, string value, long sequence)
		{ this.DesktopId = desktopId; this.Property = property; this.Value = value; this.Sequence = sequence; }
		internal Guid DesktopId { get; }
		internal VirtualDesktopPropertyKind Property { get; }
		internal string Value { get; }
		internal long Sequence { get; }
	}

	internal sealed class PropertySnapshotMismatch
	{
		internal PropertySnapshotMismatch(Guid desktopId, VirtualDesktopPropertyKind property)
		{
			this.DesktopId = desktopId;
			this.Property = property;
		}
		internal Guid DesktopId { get; }
		internal VirtualDesktopPropertyKind Property { get; }
	}
}
