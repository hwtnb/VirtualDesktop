using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDesktop.Internal;
using WindowsDesktop.Interop;

namespace WindowsDesktop
{
	public partial class VirtualDesktopProvider
	{
		private readonly object _reconciliationGate = new object();
		private IVirtualDesktopSnapshotCapture _snapshotCapture;
		private IReconciliationDelayScheduler _delayScheduler;
		private ReconciliationRetryPolicy _retryPolicy;
		private readonly Dictionary<Guid, ManagedDesktopState> _managedState = new Dictionary<Guid, ManagedDesktopState>();
		private readonly Dictionary<VirtualDesktopPropertyKey, PendingLocalWrite> _pendingWrites = new Dictionary<VirtualDesktopPropertyKey, PendingLocalWrite>();
		private readonly Dictionary<VirtualDesktopPropertyKey, EmptyPropertyCandidate> _emptyCandidates = new Dictionary<VirtualDesktopPropertyKey, EmptyPropertyCandidate>();
		private readonly Dictionary<long, ReconciliationIngressDisposition> _ingressDispositions = new Dictionary<long, ReconciliationIngressDisposition>();
		private readonly List<ReconciliationWaiter> _waiters = new List<ReconciliationWaiter>();
		private List<Guid> _stableOrder = new List<Guid>();
		private long _providerEpoch;
		private long _ingressRevision;
		private long _snapshotRevision;
		private long _localWriteGeneration;
		private long _lastPublishedIngressSequence;
		private bool _dirty;
		private bool _captureScheduled;
		private bool _captureRunning;
		private bool _runtimeInitialized;
		private bool _reconciliationShutdown;
		private int _retryAttempt;
		private int _localSetterDepth;
		private ReconciliationRetryRegistration _retryRegistration;
		private Guid? _currentDesktopId;
		private VirtualDesktopReadStatus _currentDesktopReadStatus = VirtualDesktopReadStatus.NotAttempted;
		private VirtualDesktopStableReason _pendingReason = VirtualDesktopStableReason.Initialization;
		private VirtualDesktopStableBatch _lastStableBatch;
		private bool _resetFaultPending;
		private bool _pipelineShutdownComplete;
		private Action _shutdownCleanup;
		private int _reconciliationPublicationsInFlight;
		private bool _legacyCaptureLoopRunning;

		internal long CurrentProviderEpoch { get { lock (this._reconciliationGate) return this._providerEpoch; } }
		internal long IngressRevision { get { lock (this._reconciliationGate) return this._ingressRevision; } }
		internal long SnapshotRevision { get { lock (this._reconciliationGate) return this._snapshotRevision; } }
		internal bool IsDirty { get { lock (this._reconciliationGate) return this._dirty; } }
		internal int PendingWriteCount { get { lock (this._reconciliationGate) return this._pendingWrites.Count; } }
		internal int EmptyCandidateCount { get { lock (this._reconciliationGate) return this._emptyCandidates.Count; } }
		internal int RetryAttempt { get { lock (this._reconciliationGate) return this._retryAttempt; } }
		internal VirtualDesktopStableBatch LastStableBatch { get { lock (this._reconciliationGate) return this._lastStableBatch; } }

		private void InitializeReconciliation(IVirtualDesktopSnapshotCapture snapshotCapture, IReconciliationDelayScheduler delayScheduler, ReconciliationRetryPolicy retryPolicy, bool captureAvailable)
		{
			this._snapshotCapture = snapshotCapture ?? throw new ArgumentNullException(nameof(snapshotCapture));
			this._delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
			this._retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
			this._providerEpoch = 1;
			this._dirty = true;
			this._runtimeInitialized = captureAvailable;
		}

		public Task<VirtualDesktopReconciliationResult> RequestReconciliationAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken = default(CancellationToken))
		{
			ReconciliationWaiter waiter;
			List<ReconciliationWaiter> removed;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) return Task.FromResult(VirtualDesktopReconciliationResult.ShuttingDown());
				if (cancellationToken.IsCancellationRequested) return Task.FromResult(VirtualDesktopReconciliationResult.Cancelled());
				if (!this._runtimeInitialized) return Task.FromResult(VirtualDesktopReconciliationResult.Unavailable(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable));
				this.AdvanceIngressRevisionUnderLock();
				this.MarkDirtyUnderLock(reason);
				waiter = new ReconciliationWaiter(this._providerEpoch, this._ingressRevision, cancellationToken);
				this._waiters.Add(waiter);
				removed = this.RemoveCompletedWaitersUnderLock();
			}
			foreach (var completed in removed) completed.DisposeCancellation();
			this.CancelRetryAndScheduleCapture();
			return waiter.Completion.Task;
		}

		internal VirtualDesktopCallbackDto StampCallback(VirtualDesktopCallbackDto dto)
		{
			if (dto == null) throw new ArgumentNullException(nameof(dto));
			if (dto.ProviderEpoch != 0) return dto;
			lock (this._reconciliationGate) return dto.WithProviderEpoch(this._providerEpoch);
		}

		internal void OnIngressAccepted(VirtualDesktopCallbackDto dto, long sequence)
		{
			try
			{
				lock (this._reconciliationGate)
				{
					var disposition = this.ClassifyAcceptedIngressUnderLock(dto);
					this._ingressDispositions[sequence] = disposition;
				}
			}
			catch
			{
				lock (this._reconciliationGate)
				{
					this.AdvanceIngressRevisionUnderLock();
					this.MarkDirtyUnderLock(VirtualDesktopStableReason.Recovery);
					this._ingressDispositions[sequence] = ReconciliationIngressDisposition.Reconcile;
				}
			}
		}

		internal bool OnIngressProcessing(VirtualDesktopCallbackDto dto, long sequence)
		{
			ReconciliationIngressDisposition disposition;
			VirtualDesktopCurrentTransition transition = null;
			lock (this._reconciliationGate)
			{
				if (dto.ProviderEpoch != this._providerEpoch)
				{
					disposition = ReconciliationIngressDisposition.OldEpoch;
					this._ingressDispositions[sequence] = disposition;
				}
				else if (!this._ingressDispositions.TryGetValue(sequence, out disposition)) disposition = ReconciliationIngressDisposition.None;
				if (disposition == ReconciliationIngressDisposition.CurrentOnly)
				{
					if (dto.ProviderEpoch == this._providerEpoch && !this._dirty && !this._captureRunning && !this._captureScheduled && dto.RelatedDesktopId.HasValue && this._stableOrder.Contains(dto.RelatedDesktopId.Value) && this._snapshotRevision > 0 && sequence > this._lastPublishedIngressSequence)
					{
						this._currentDesktopId = dto.RelatedDesktopId.Value;
						this._currentDesktopReadStatus = VirtualDesktopReadStatus.Success;
						this._lastPublishedIngressSequence = sequence;
						transition = new VirtualDesktopCurrentTransition(this._providerEpoch, sequence, this._snapshotRevision, dto.RelatedDesktopId.Value);
						this._reconciliationPublicationsInFlight++;
					}
					else
					{
						this.AdvanceIngressRevisionUnderLock();
						this.MarkDirtyUnderLock(VirtualDesktopStableReason.Recovery);
						this._ingressDispositions[sequence] = ReconciliationIngressDisposition.Reconcile;
					}
				}
			}
			if (transition != null)
			{
				this.PublishCurrentTransition(transition);
			}
			return disposition != ReconciliationIngressDisposition.OldEpoch;
		}

		internal void OnIngressProcessed(VirtualDesktopCallbackDto dto, long sequence)
		{
			ReconciliationIngressDisposition disposition;
			lock (this._reconciliationGate)
			{
				if (!this._ingressDispositions.TryGetValue(sequence, out disposition)) return;
				this._ingressDispositions.Remove(sequence);
			}
			if (disposition == ReconciliationIngressDisposition.Reconcile || disposition == ReconciliationIngressDisposition.Acknowledged) this.CancelRetryAndScheduleCapture();
			else if (disposition == ReconciliationIngressDisposition.OldEpoch)
				this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.OldEpochDiscarded, VirtualDesktopProviderFaultPhase.MirrorTransition, VirtualDesktopEventPipeline.ToPublicKind(dto.Kind), dto.DesktopId, sequence, typeof(InvalidOperationException));
			else if (disposition == ReconciliationIngressDisposition.CurrentOnly) this.ReleaseReconciliationPublication();
		}

		internal void OnMaterializationFailure()
		{
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) return;
				this.AdvanceIngressRevisionUnderLock();
				this.MarkDirtyUnderLock(VirtualDesktopStableReason.Recovery);
			}
			this.CancelRetryAndScheduleCapture();
		}

		internal void OnRuntimeInitialized()
		{
			bool reportReset;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) return;
				this._runtimeInitialized = true;
				this.MarkDirtyUnderLock(VirtualDesktopStableReason.Initialization);
				reportReset = this._resetFaultPending;
				this._resetFaultPending = false;
			}
			if (reportReset) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.ProviderReset, VirtualDesktopProviderFaultPhase.MirrorTransition, VirtualDesktopProviderEventKind.Unknown, null, 0, typeof(InvalidOperationException));
			this.ScheduleCapture();
		}

		internal void BindDesktopToCurrentEpoch(VirtualDesktop desktop)
		{
			if (desktop == null) throw new ArgumentNullException(nameof(desktop));
			lock (this._reconciliationGate) desktop.BindProviderEpoch(this._providerEpoch);
		}

		internal void ValidateDesktopEpoch(VirtualDesktop desktop)
		{
			if (desktop == null) throw new ArgumentNullException(nameof(desktop));
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) throw new ObjectDisposedException(nameof(VirtualDesktopProvider));
				if (desktop.ProviderEpoch != this._providerEpoch) throw new InvalidOperationException("The virtual desktop belongs to an inactive provider epoch.");
			}
		}

		internal void SeedManagedDesktop(VirtualDesktop desktop)
		{
			lock (this._reconciliationGate)
			{
				if (desktop.ProviderEpoch != this._providerEpoch) return;
				if (!this._managedState.TryGetValue(desktop.Id, out var state)) this._managedState.Add(desktop.Id, state = new ManagedDesktopState(desktop.Id));
				if (desktop.Name != null) { state.Name.Value = desktop.Name; state.Name.HasValue = true; state.Name.Status = VirtualDesktopReadStatus.Success; }
				if (desktop.WallpaperPath != null) { state.Wallpaper.Value = desktop.WallpaperPath; state.Wallpaper.HasValue = true; state.Wallpaper.Status = VirtualDesktopReadStatus.Success; }
			}
		}

		internal void RecordLocalWrite(VirtualDesktop desktop, VirtualDesktopPropertyKind property, string value)
		{
			IDisposable retry;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown || desktop.ProviderEpoch != this._providerEpoch) return;
				if (!this._managedState.TryGetValue(desktop.Id, out var state)) this._managedState.Add(desktop.Id, state = new ManagedDesktopState(desktop.Id));
				var propertyState = property == VirtualDesktopPropertyKind.Name ? state.Name : state.Wallpaper;
				propertyState.Value = value ?? string.Empty;
				propertyState.HasValue = true;
				propertyState.Status = VirtualDesktopReadStatus.Success;
				var key = new VirtualDesktopPropertyKey(desktop.Id, property);
				this._emptyCandidates.Remove(key);
				this.AdvanceIngressRevisionUnderLock();
				this._pendingWrites[key] = new PendingLocalWrite(this._providerEpoch, desktop.Id, property, this.NextCounterUnderLock(ref this._localWriteGeneration), value ?? string.Empty, this._ingressRevision);
				this.MarkDirtyUnderLock(VirtualDesktopStableReason.LocalWrite);
				retry = this.TakeRetryUnderLock();
			}
			retry?.Dispose();
			this.ScheduleCapture();
		}

		internal void EnterLocalSetter()
		{
			lock (this._reconciliationGate) this._localSetterDepth++;
		}

		internal void ExitLocalSetter()
		{
			bool schedule;
			lock (this._reconciliationGate)
			{
				if (this._localSetterDepth > 0) this._localSetterDepth--;
				schedule = this._localSetterDepth == 0 && this._dirty;
			}
			if (schedule) this.ScheduleCapture();
		}

		internal long ResetRuntime(bool scheduleReconciliation = true)
		{
			List<ReconciliationWaiter> waiters;
			IDisposable retry;
			long newEpoch;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) return this._providerEpoch;
				newEpoch = this.NextCounterUnderLock(ref this._providerEpoch);
				this._ingressRevision = 0;
				this._snapshotRevision = 0;
				this._localWriteGeneration = 0;
				this._lastPublishedIngressSequence = 0;
				this._managedState.Clear();
				this._stableOrder.Clear();
				this._pendingWrites.Clear();
				this._emptyCandidates.Clear();
				this._lastStableBatch = null;
				this._currentDesktopId = null;
				this._currentDesktopReadStatus = VirtualDesktopReadStatus.NotAttempted;
				this._dirty = true;
				if (!scheduleReconciliation) this._runtimeInitialized = false;
				this._captureScheduled = false;
				this._retryAttempt = 0;
				this._pendingReason = VirtualDesktopStableReason.Recovery;
				retry = this.TakeRetryUnderLock();
				waiters = this.TakeWaitersUnderLock();
			}
			retry?.Dispose();
			foreach (var waiter in waiters) waiter.Complete(VirtualDesktopReconciliationResult.SupersededByReset(newEpoch));
			if (this._runtime is IVirtualDesktopProviderRuntimeReset resettable) resettable.ResetManagedDesktops();
			if (scheduleReconciliation) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.ProviderReset, VirtualDesktopProviderFaultPhase.MirrorTransition, VirtualDesktopProviderEventKind.Unknown, null, 0, typeof(InvalidOperationException));
			else lock (this._reconciliationGate) this._resetFaultPending = true;
			if (scheduleReconciliation) this.ScheduleCapture();
			return newEpoch;
		}

		private ReconciliationIngressDisposition ClassifyAcceptedIngressUnderLock(VirtualDesktopCallbackDto dto)
		{
			if (this._reconciliationShutdown) return ReconciliationIngressDisposition.None;
			if (dto.ProviderEpoch != this._providerEpoch) return ReconciliationIngressDisposition.OldEpoch;
			switch (dto.Kind)
			{
				case VirtualDesktopCallbackKind.Created:
				case VirtualDesktopCallbackKind.Destroyed:
				case VirtualDesktopCallbackKind.Moved:
					this.AdvanceIngressRevisionUnderLock();
					this.MarkDirtyUnderLock(VirtualDesktopStableReason.TopologyChanged);
					return ReconciliationIngressDisposition.Reconcile;
				case VirtualDesktopCallbackKind.Renamed:
					return this.ClassifyPropertyIngressUnderLock(dto.DesktopId, VirtualDesktopPropertyKind.Name, dto.Value);
				case VirtualDesktopCallbackKind.WallpaperChanged:
					return this.ClassifyPropertyIngressUnderLock(dto.DesktopId, VirtualDesktopPropertyKind.WallpaperPath, dto.Value);
				case VirtualDesktopCallbackKind.CurrentChanged:
					if (!this._dirty && !this._captureRunning && !this._captureScheduled && dto.RelatedDesktopId.HasValue && this._stableOrder.Contains(dto.RelatedDesktopId.Value) && this._snapshotRevision > 0) return ReconciliationIngressDisposition.CurrentOnly;
					this.AdvanceIngressRevisionUnderLock();
					this.MarkDirtyUnderLock(VirtualDesktopStableReason.Recovery);
					return ReconciliationIngressDisposition.Reconcile;
				default:
					return ReconciliationIngressDisposition.None;
			}
		}

		private ReconciliationIngressDisposition ClassifyPropertyIngressUnderLock(Guid? desktopId, VirtualDesktopPropertyKind property, string value)
		{
			if (!desktopId.HasValue)
			{
				this.AdvanceIngressRevisionUnderLock();
				this.MarkDirtyUnderLock(VirtualDesktopStableReason.PropertyChanged);
				return ReconciliationIngressDisposition.Reconcile;
			}
			var key = new VirtualDesktopPropertyKey(desktopId.Value, property);
			if (this._pendingWrites.TryGetValue(key, out var pending) && pending.ProviderEpoch == this._providerEpoch && string.Equals(pending.TargetValue, value ?? string.Empty, StringComparison.Ordinal))
			{
				pending.AcknowledgementObserved = true;
				this.AdvanceIngressRevisionUnderLock();
				this.MarkDirtyUnderLock(VirtualDesktopStableReason.LocalWrite);
				return ReconciliationIngressDisposition.Acknowledged;
			}
			if (!this._pendingWrites.ContainsKey(key) && this._managedState.TryGetValue(desktopId.Value, out var state))
			{
				var current = property == VirtualDesktopPropertyKind.Name ? state.Name : state.Wallpaper;
				if (current.HasValue && string.Equals(current.Value, value ?? string.Empty, StringComparison.Ordinal)) return ReconciliationIngressDisposition.Duplicate;
			}
			this.AdvanceIngressRevisionUnderLock();
			this.MarkDirtyUnderLock(VirtualDesktopStableReason.PropertyChanged);
			return ReconciliationIngressDisposition.Reconcile;
		}

		private void ScheduleCapture()
		{
			bool schedule;
			bool runLegacyLoop = false;
			var pipeline = this.EventPipeline;
			lock (this._reconciliationGate)
			{
				schedule = this._runtimeInitialized && !this._reconciliationShutdown && this._localSetterDepth == 0 && this._reconciliationPublicationsInFlight == 0 && this._dirty && !this._captureScheduled && !this._captureRunning && this._retryRegistration == null;
				if (schedule) this._captureScheduled = true;
				if (schedule && pipeline.Mode == VirtualDesktopEventMode.LegacyInline && !this._legacyCaptureLoopRunning)
				{
					this._legacyCaptureLoopRunning = true;
					runLegacyLoop = true;
				}
			}
			if (!schedule) return;
			if (pipeline.Mode == VirtualDesktopEventMode.LegacyInline)
			{
				if (runLegacyLoop) this.RunLegacyCaptureLoop();
				return;
			}
			EventScheduledOperation operation;
			var postingThread = Environment.CurrentManagedThreadId;
			var posting = true;
			var inline = false;
			try
			{
				operation = pipeline.PostOwner(() =>
				{
					if (pipeline.Mode == VirtualDesktopEventMode.StrongScheduled && posting && Environment.CurrentManagedThreadId == postingThread) { inline = true; return; }
					this.RunCapture();
				});
			}
			catch (Exception ex) { this.OnCaptureScheduleFailure(ex); return; }
			finally { posting = false; }
			if (inline) { this.OnCaptureScheduleFailure(new InvalidOperationException("A strong reconciliation scheduler executed the capture inline.")); return; }
			if (operation == null || operation.InitialStatus == EventScheduleInitialStatus.Rejected) { this.OnCaptureScheduleFailure(new InvalidOperationException("The reconciliation scheduler rejected the capture.")); return; }
			operation.ObserveTerminal(lifecycle =>
			{
				if (lifecycle != EventScheduleLifecycle.Aborted) return;
				this.OnCaptureScheduleFailure(new InvalidOperationException("The accepted reconciliation operation was aborted."));
			});
		}

		private void RunLegacyCaptureLoop()
		{
			try
			{
				while (true)
				{
					this.RunCapture();
					lock (this._reconciliationGate)
					{
						if (this._captureScheduled) continue;
						this._legacyCaptureLoopRunning = false;
						return;
					}
				}
			}
			finally
			{
				lock (this._reconciliationGate) this._legacyCaptureLoopRunning = false;
			}
		}

		private void RunCapture()
		{
			long epoch;
			long targetRevision;
			VirtualDesktopStableReason reason;
			lock (this._reconciliationGate)
			{
				this._captureScheduled = false;
				if (this._reconciliationShutdown || !this._runtimeInitialized || !this._dirty || this._captureRunning) return;
				this._captureRunning = true;
				epoch = this._providerEpoch;
				targetRevision = this._ingressRevision;
				reason = this._pendingReason;
			}

			VirtualDesktopSnapshotBatch snapshot = null;
			Exception captureError = null;
			try { snapshot = this._snapshotCapture.Capture(); }
			catch (Exception ex) { captureError = ex; }
			if (captureError != null || snapshot == null)
			{
				lock (this._reconciliationGate) this._captureRunning = false;
				this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.StructuralSnapshot, VirtualDesktopProviderFaultPhase.SnapshotCapture, VirtualDesktopProviderEventKind.StableBatch, null, targetRevision, captureError?.GetType() ?? typeof(InvalidOperationException));
				this.ScheduleRetryOrCompleteUnavailable(VirtualDesktopProviderFailureCategory.StructuralSnapshot);
				this.TryCompleteReconciliationShutdown();
				return;
			}
			this.CommitCapture(epoch, targetRevision, reason, snapshot, false);
		}

		private void CommitCapture(long epoch, long targetRevision, VirtualDesktopStableReason reason, VirtualDesktopSnapshotBatch snapshot, bool allowShutdown)
		{
			var transitions = new List<PropertyMirrorTransition>();
			var mismatches = new List<PropertySnapshotMismatch>();
			var completed = new List<ReconciliationWaiter>();
			var cancelled = new List<ReconciliationWaiter>();
			VirtualDesktopStableBatch batch = null;
			bool retry = false;
			bool stale = false;
			VirtualDesktopProviderFailureCategory failure = VirtualDesktopProviderFailureCategory.Unknown;
			lock (this._reconciliationGate)
			{
				this._captureRunning = false;
				if ((this._reconciliationShutdown && !allowShutdown) || epoch != this._providerEpoch || targetRevision != this._ingressRevision)
				{
					stale = true;
					this._dirty = true;
				}
				else if (!IsStructurallyComplete(snapshot))
				{
					this._dirty = true;
					retry = true;
					failure = VirtualDesktopProviderFailureCategory.StructuralSnapshot;
				}
				else
				{
					var nextRevision = this.NextCounterUnderLock(ref this._snapshotRevision);
					var entries = new List<VirtualDesktopStableEntry>(snapshot.Desktops.Count);
					var seen = new HashSet<Guid>();
					foreach (var raw in snapshot.Desktops.OrderBy(x => x.OrderIndex))
					{
						seen.Add(raw.Id);
						if (!this._managedState.TryGetValue(raw.Id, out var state)) this._managedState.Add(raw.Id, state = new ManagedDesktopState(raw.Id));
						state.OrderIndex = raw.OrderIndex;
						retry |= this.ApplyPropertyPolicyUnderLock(state, VirtualDesktopPropertyKind.Name, raw.Name, nextRevision, transitions, mismatches, targetRevision);
						retry |= this.ApplyPropertyPolicyUnderLock(state, VirtualDesktopPropertyKind.WallpaperPath, raw.WallpaperPath, nextRevision, transitions, mismatches, targetRevision);
						entries.Add(new VirtualDesktopStableEntry(raw.Id, raw.OrderIndex, state.Name.HasValue ? state.Name.Value : null, raw.Name.Status, state.Wallpaper.HasValue ? state.Wallpaper.Value : null, raw.WallpaperPath.Status));
					}
					foreach (var removed in this._managedState.Keys.Where(id => !seen.Contains(id)).ToArray())
					{
						this._managedState.Remove(removed);
						this.RemovePropertyStateUnderLock(removed);
					}
					this._stableOrder = entries.Select(x => x.Id).ToList();
					if (snapshot.CurrentDesktopId.Status == VirtualDesktopReadStatus.Success && this._stableOrder.Contains(snapshot.CurrentDesktopId.Value))
					{
						this._currentDesktopId = snapshot.CurrentDesktopId.Value;
						this._currentDesktopReadStatus = VirtualDesktopReadStatus.Success;
					}
					else
					{
						this._currentDesktopReadStatus = snapshot.CurrentDesktopId.Status == VirtualDesktopReadStatus.Success ? VirtualDesktopReadStatus.Failed : snapshot.CurrentDesktopId.Status;
						if (!this._currentDesktopId.HasValue || !this._stableOrder.Contains(this._currentDesktopId.Value)) this._currentDesktopId = null;
						if (snapshot.CurrentDesktopId.Status != VirtualDesktopReadStatus.Unsupported) retry = true;
					}
					batch = new VirtualDesktopStableBatch(epoch, nextRevision, this._currentDesktopId, this._currentDesktopReadStatus, entries, reason);
					this._lastStableBatch = batch;
					this._dirty = retry;
					this._pendingReason = retry ? VirtualDesktopStableReason.Recovery : VirtualDesktopStableReason.ExplicitReconciliation;
					if (!retry) this._retryAttempt = 0;
					for (var i = this._waiters.Count - 1; i >= 0; i--)
					{
						var waiter = this._waiters[i];
						if (waiter.Completion.Task.IsCompleted) { cancelled.Add(waiter); this._waiters.RemoveAt(i); continue; }
						if (waiter.Epoch == epoch && waiter.TargetRevision <= targetRevision) { completed.Add(waiter); this._waiters.RemoveAt(i); }
					}
					this._reconciliationPublicationsInFlight++;
				}
			}

			if (stale) { this.ScheduleCapture(); this.TryCompleteReconciliationShutdown(); return; }
			if (batch == null)
			{
				this.ReportReconciliationFault(failure, VirtualDesktopProviderFaultPhase.SnapshotCapture, VirtualDesktopProviderEventKind.StableBatch, null, targetRevision, typeof(InvalidOperationException));
				this.ScheduleRetryOrCompleteUnavailable(failure);
				this.TryCompleteReconciliationShutdown();
				return;
			}
			try
			{
				foreach (var waiter in cancelled) waiter.DisposeCancellation();
				foreach (var transition in transitions) this.ApplyMirrorTransition(transition);
				foreach (var mismatch in mismatches)
					this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.PropertySnapshot, VirtualDesktopProviderFaultPhase.SnapshotCapture, mismatch.Property == VirtualDesktopPropertyKind.Name ? VirtualDesktopProviderEventKind.Renamed : VirtualDesktopProviderEventKind.WallpaperChanged, mismatch.DesktopId, targetRevision, typeof(InvalidOperationException));
				this.PublishStableBatch(batch, targetRevision);
				foreach (var waiter in completed) waiter.Complete(VirtualDesktopReconciliationResult.Succeeded(batch));
				if (retry) this.ScheduleRetryOrCompleteUnavailable(VirtualDesktopProviderFailureCategory.PropertySnapshot);
			}
			finally
			{
				this.ReleaseReconciliationPublication();
				this.TryCompleteReconciliationShutdown();
			}
		}

		private bool ApplyPropertyPolicyUnderLock(ManagedDesktopState desktop, VirtualDesktopPropertyKind property, VirtualDesktopReadResult<string> raw, long snapshotRevision, List<PropertyMirrorTransition> transitions, List<PropertySnapshotMismatch> mismatches, long sequence)
		{
			var key = new VirtualDesktopPropertyKey(desktop.Id, property);
			var state = property == VirtualDesktopPropertyKind.Name ? desktop.Name : desktop.Wallpaper;
			if (raw.Status == VirtualDesktopReadStatus.Unsupported)
			{
				state.Status = VirtualDesktopReadStatus.Unsupported;
				this._emptyCandidates.Remove(key);
				this._pendingWrites.Remove(key);
				return false;
			}
			if (raw.Status == VirtualDesktopReadStatus.Failed || raw.Status == VirtualDesktopReadStatus.NotAttempted)
			{
				state.Status = raw.Status;
				return true;
			}
			var candidate = raw.Value ?? string.Empty;
			if (this._pendingWrites.TryGetValue(key, out var pending))
			{
				this._pendingWrites.Remove(key);
				if (!string.Equals(candidate, pending.TargetValue, StringComparison.Ordinal)) mismatches.Add(new PropertySnapshotMismatch(desktop.Id, property));
			}
			if (candidate.Length != 0)
			{
				this._emptyCandidates.Remove(key);
				return this.CommitEffectiveValueUnderLock(desktop, property, state, candidate, transitions, sequence);
			}
			if (!state.HasValue || string.IsNullOrEmpty(state.Value))
			{
				this._emptyCandidates.Remove(key);
				return this.CommitEffectiveValueUnderLock(desktop, property, state, string.Empty, transitions, sequence);
			}
			if (!this._emptyCandidates.TryGetValue(key, out var empty) || empty.ProviderEpoch != this._providerEpoch)
			{
				this._emptyCandidates[key] = new EmptyPropertyCandidate(this._providerEpoch, snapshotRevision);
				state.Status = VirtualDesktopReadStatus.Success;
				return true;
			}
			empty.SuccessfulEmptyReads++;
			if (empty.SuccessfulEmptyReads < 2) return true;
			this._emptyCandidates.Remove(key);
			return this.CommitEffectiveValueUnderLock(desktop, property, state, string.Empty, transitions, sequence);
		}

		private bool CommitEffectiveValueUnderLock(ManagedDesktopState desktop, VirtualDesktopPropertyKind property, ManagedDesktopPropertyState state, string value, List<PropertyMirrorTransition> transitions, long sequence)
		{
			var changed = !state.HasValue || !string.Equals(state.Value, value, StringComparison.Ordinal);
			state.Value = value;
			state.HasValue = true;
			state.Status = VirtualDesktopReadStatus.Success;
			if (changed) transitions.Add(new PropertyMirrorTransition(desktop.Id, property, value, sequence));
			return false;
		}

		private void ApplyMirrorTransition(PropertyMirrorTransition transition)
		{
			if (!this.TryResolveDesktop(transition.DesktopId, true, out var desktop, out var error))
			{
				if (error != null) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.PropertySnapshot, VirtualDesktopProviderFaultPhase.MirrorTransition, transition.Property == VirtualDesktopPropertyKind.Name ? VirtualDesktopProviderEventKind.Renamed : VirtualDesktopProviderEventKind.WallpaperChanged, transition.DesktopId, transition.Sequence, error.GetType());
				return;
			}
			if (desktop.ProviderEpoch != this.CurrentProviderEpoch) return;
			if (transition.Property == VirtualDesktopPropertyKind.Name) this.EventPipeline.ApplyReconciledName(desktop, transition.Value, transition.Sequence);
			else this.EventPipeline.ApplyReconciledWallpaper(desktop, transition.Value, transition.Sequence);
		}

		private void PublishStableBatch(VirtualDesktopStableBatch batch, long sequence)
		{
			var handlers = this.StableBatchPublished;
			if (handlers == null) return;
			var exceptions = new ExceptionCollector();
			foreach (EventHandler<VirtualDesktopStableBatch> handler in handlers.GetInvocationList())
				this.EventPipeline.InvokeSubscriber(handler, () => handler(this, batch), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.StableBatch, null, sequence, exceptions);
			foreach (var exception in exceptions.Items) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.Subscriber, VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.StableBatch, null, sequence, exception.GetType());
		}

		private void PublishCurrentTransition(VirtualDesktopCurrentTransition transition)
		{
			var handlers = this.CurrentTransitioned;
			if (handlers == null) return;
			var exceptions = new ExceptionCollector();
			foreach (EventHandler<VirtualDesktopCurrentTransition> handler in handlers.GetInvocationList())
				this.EventPipeline.InvokeSubscriber(handler, () => handler(this, transition), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.CurrentTransition, transition.CurrentDesktopId, transition.IngressSequence, exceptions);
			foreach (var exception in exceptions.Items) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.Subscriber, VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.CurrentTransition, transition.CurrentDesktopId, transition.IngressSequence, exception.GetType());
		}

		private void ReleaseReconciliationPublication()
		{
			Action cleanup;
			bool schedule;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationPublicationsInFlight > 0) this._reconciliationPublicationsInFlight--;
				cleanup = this.TakeShutdownCleanupIfReadyUnderLock();
				schedule = this._reconciliationPublicationsInFlight == 0 && !this._reconciliationShutdown && this._dirty;
			}
			this.InvokeShutdownCleanup(cleanup);
			if (schedule) this.ScheduleCapture();
		}

		private void ScheduleRetryOrCompleteUnavailable(VirtualDesktopProviderFailureCategory failureCategory)
		{
			TimeSpan delay = TimeSpan.Zero;
			bool exhausted;
			ReconciliationRetryRegistration registration = null;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown || !this._dirty || this._retryRegistration != null) return;
				exhausted = this._retryAttempt >= this._retryPolicy.MaximumRetries;
				if (!exhausted)
				{
					delay = this._retryPolicy.GetDelay(this._retryAttempt++);
					registration = new ReconciliationRetryRegistration(this.OnRetryDue);
					this._retryRegistration = registration;
				}
			}
			if (exhausted)
			{
				this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.RetryExhausted, VirtualDesktopProviderFaultPhase.SnapshotCapture, VirtualDesktopProviderEventKind.StableBatch, null, 0, typeof(InvalidOperationException));
				this.CompleteWaitersUnavailable(failureCategory);
				return;
			}
			try { registration.Attach(this._delayScheduler.Schedule(delay, registration.Fire)); }
			catch (Exception ex)
			{
				lock (this._reconciliationGate) if (ReferenceEquals(this._retryRegistration, registration)) this._retryRegistration = null;
				registration.Dispose();
				this.OnCaptureScheduleFailure(ex);
			}
		}

		private void OnRetryDue(ReconciliationRetryRegistration registration)
		{
			lock (this._reconciliationGate)
			{
				if (!ReferenceEquals(this._retryRegistration, registration)) return;
				this._retryRegistration = null;
			}
			this.ScheduleCapture();
		}

		private void OnCaptureScheduleFailure(Exception exception)
		{
			lock (this._reconciliationGate) { this._captureScheduled = false; this._dirty = true; }
			this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.Scheduler, VirtualDesktopProviderFaultPhase.Scheduling, VirtualDesktopProviderEventKind.StableBatch, null, 0, exception.GetType());
			this.ScheduleRetryOrCompleteUnavailable(VirtualDesktopProviderFailureCategory.Scheduler);
		}

		private void CancelRetryAndScheduleCapture()
		{
			IDisposable retry;
			lock (this._reconciliationGate) { this._retryAttempt = 0; retry = this.TakeRetryUnderLock(); }
			retry?.Dispose();
			this.ScheduleCapture();
		}

		private void CompleteWaitersUnavailable(VirtualDesktopProviderFailureCategory category)
		{
			List<ReconciliationWaiter> waiters;
			lock (this._reconciliationGate) waiters = this.TakeWaitersUnderLock();
			foreach (var waiter in waiters) waiter.Complete(VirtualDesktopReconciliationResult.Unavailable(category));
			if (waiters.Count != 0) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable, VirtualDesktopProviderFaultPhase.SnapshotCapture, VirtualDesktopProviderEventKind.StableBatch, null, 0, typeof(InvalidOperationException));
		}

		private void BeginReconciliationShutdown(bool ownerAccess)
		{
			List<ReconciliationWaiter> waiters;
			IDisposable retry;
			bool undrained;
			bool finalCapture;
			lock (this._reconciliationGate)
			{
				if (this._reconciliationShutdown) return;
				finalCapture = this._runtimeInitialized && this._dirty && !this._captureRunning && this._reconciliationPublicationsInFlight == 0 && ownerAccess;
				this._reconciliationShutdown = true;
				retry = this.TakeRetryUnderLock();
				waiters = this.TakeWaitersUnderLock();
				undrained = this._dirty || this._captureRunning || this._captureScheduled || this._ingressDispositions.Count != 0;
				this._captureScheduled = false;
			}
			retry?.Dispose();
			foreach (var waiter in waiters) waiter.Complete(VirtualDesktopReconciliationResult.ShuttingDown());
			if (finalCapture) this.RunFinalCapture();
			lock (this._reconciliationGate) undrained = this._dirty || this._captureRunning || this._captureScheduled || this._ingressDispositions.Count != 0;
			if (undrained) this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.ShutdownUndrained, VirtualDesktopProviderFaultPhase.Shutdown, VirtualDesktopProviderEventKind.Unknown, null, 0, typeof(InvalidOperationException));
		}

		private void CompleteReconciliationShutdown(Action cleanup)
		{
			Action ready;
			lock (this._reconciliationGate)
			{
				this._pipelineShutdownComplete = true;
				this._shutdownCleanup += cleanup;
				ready = this.TakeShutdownCleanupIfReadyUnderLock();
			}
			this.InvokeShutdownCleanup(ready);
		}

		private void TryCompleteReconciliationShutdown()
		{
			Action ready;
			lock (this._reconciliationGate) ready = this.TakeShutdownCleanupIfReadyUnderLock();
			this.InvokeShutdownCleanup(ready);
		}

		private Action TakeShutdownCleanupIfReadyUnderLock()
		{
			if (!this._reconciliationShutdown || !this._pipelineShutdownComplete || this._captureRunning || this._reconciliationPublicationsInFlight != 0) return null;
			var ready = this._shutdownCleanup;
			this._shutdownCleanup = null;
			return ready;
		}

		private void InvokeShutdownCleanup(Action cleanup)
		{
			if (cleanup == null) return;
			try { cleanup(); }
			catch (Exception ex) { this.ReportReconciliationFault(VirtualDesktopProviderFailureCategory.ShutdownUndrained, VirtualDesktopProviderFaultPhase.Shutdown, VirtualDesktopProviderEventKind.Unknown, null, 0, ex.GetType()); }
		}

		private void RunFinalCapture()
		{
			long epoch;
			long targetRevision;
			VirtualDesktopStableReason reason;
			lock (this._reconciliationGate)
			{
				if (this._captureRunning || !this._dirty) return;
				this._captureRunning = true;
				epoch = this._providerEpoch;
				targetRevision = this._ingressRevision;
				reason = this._pendingReason;
			}
			VirtualDesktopSnapshotBatch snapshot = null;
			try { snapshot = this._snapshotCapture.Capture(); }
			catch { }
			if (snapshot == null) { lock (this._reconciliationGate) this._captureRunning = false; this.TryCompleteReconciliationShutdown(); return; }
			this.CommitCapture(epoch, targetRevision, reason, snapshot, true);
		}

		private void ReportReconciliationFault(VirtualDesktopProviderFailureCategory category, VirtualDesktopProviderFaultPhase phase, VirtualDesktopProviderEventKind kind, Guid? desktopId, long sequence, Type exceptionType)
			=> this.ReportEventFault(new VirtualDesktopProviderFault(phase, kind, desktopId, exceptionType?.FullName, null, sequence, category));

		private void MarkDirtyUnderLock(VirtualDesktopStableReason reason)
		{
			this._dirty = true;
			if (ReasonPriority(reason) >= ReasonPriority(this._pendingReason)) this._pendingReason = reason;
		}

		private void AdvanceIngressRevisionUnderLock() => this.NextCounterUnderLock(ref this._ingressRevision);
		private long NextCounterUnderLock(ref long value)
		{
			if (value == long.MaxValue) throw new InvalidOperationException("A provider revision counter was exhausted.");
			return ++value;
		}

		private ReconciliationRetryRegistration TakeRetryUnderLock() { var retry = this._retryRegistration; this._retryRegistration = null; return retry; }
		private List<ReconciliationWaiter> TakeWaitersUnderLock() { var result = new List<ReconciliationWaiter>(this._waiters); this._waiters.Clear(); return result; }
		private List<ReconciliationWaiter> RemoveCompletedWaitersUnderLock()
		{
			var removed = new List<ReconciliationWaiter>();
			for (var i = this._waiters.Count - 1; i >= 0; i--)
			{
				if (!this._waiters[i].Completion.Task.IsCompleted) continue;
				removed.Add(this._waiters[i]);
				this._waiters.RemoveAt(i);
			}
			return removed;
		}

		private void RemovePropertyStateUnderLock(Guid desktopId)
		{
			foreach (var key in this._pendingWrites.Keys.Where(x => x.DesktopId == desktopId).ToArray()) this._pendingWrites.Remove(key);
			foreach (var key in this._emptyCandidates.Keys.Where(x => x.DesktopId == desktopId).ToArray()) this._emptyCandidates.Remove(key);
		}

		private static int ReasonPriority(VirtualDesktopStableReason reason)
		{
			switch (reason)
			{
				case VirtualDesktopStableReason.TopologyChanged: return 6;
				case VirtualDesktopStableReason.LocalWrite: return 5;
				case VirtualDesktopStableReason.PropertyChanged: return 4;
				case VirtualDesktopStableReason.Recovery: return 3;
				case VirtualDesktopStableReason.ExplicitReconciliation: return 2;
				default: return 1;
			}
		}

		private static bool IsStructurallyComplete(VirtualDesktopSnapshotBatch snapshot)
		{
			if (snapshot == null || snapshot.Enumeration.Status != VirtualDesktopReadStatus.Success || snapshot.Enumeration.Value < 0 || snapshot.StructuralFailures.Count != 0 || snapshot.Enumeration.Value != snapshot.Desktops.Count) return false;
			var ids = new HashSet<Guid>();
			for (var index = 0; index < snapshot.Desktops.Count; index++)
			{
				var entry = snapshot.Desktops[index];
				if (entry == null || entry.Id == Guid.Empty || entry.OrderIndex != index || !ids.Add(entry.Id)) return false;
			}
			return true;
		}
	}
}
