using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace WindowsDesktop.Internal
{
	internal enum EventScheduleInitialStatus { Accepted, Rejected }
	internal enum EventScheduleLifecycle { Rejected, Accepted, Started, Completed, Aborted }
	internal enum EventPumpState { Ready, Draining, Undrained, Faulted, Shutdown }
	internal enum EventEnqueueStatus { Accepted, Rejected }
	internal enum EventEnqueueRejectionReason { None, Shutdown, CapacityExceeded }

	internal sealed class EventScheduledOperation
	{
		private readonly object _gate = new object();
		private EventScheduleLifecycle _lifecycle;
		private Func<bool> _abort;
		private Action<EventScheduleLifecycle> _terminalObservers;

		internal EventScheduledOperation(EventScheduleInitialStatus initialStatus)
		{
			this.InitialStatus = initialStatus;
			this._lifecycle = initialStatus == EventScheduleInitialStatus.Accepted ? EventScheduleLifecycle.Accepted : EventScheduleLifecycle.Rejected;
		}

		internal EventScheduleInitialStatus InitialStatus { get; }
		internal EventScheduleLifecycle Lifecycle { get { lock (this._gate) return this._lifecycle; } }
		internal event Action<EventScheduleLifecycle> LifecycleChanged;

		internal void MarkStarted() => this.Transition(EventScheduleLifecycle.Started);
		internal void MarkCompleted() => this.Transition(EventScheduleLifecycle.Completed);
		internal void MarkAborted() => this.Transition(EventScheduleLifecycle.Aborted);
		internal void SetAbort(Func<bool> abort) { lock (this._gate) this._abort = abort; }
		internal bool TryAbort() { Func<bool> abort; lock (this._gate) abort = this._abort; return abort != null && abort(); }

		internal void ObserveTerminal(Action<EventScheduleLifecycle> observer)
		{
			if (observer == null) throw new ArgumentNullException(nameof(observer));
			EventScheduleLifecycle? terminal = null;
			lock (this._gate)
			{
				if (IsTerminal(this._lifecycle)) terminal = this._lifecycle;
				else this._terminalObservers += observer;
			}
			if (terminal.HasValue) SafeInvoke(observer, terminal.Value);
		}

		private void Transition(EventScheduleLifecycle next)
		{
			Action<EventScheduleLifecycle> changed;
			Action<EventScheduleLifecycle> terminalObservers = null;
			lock (this._gate)
			{
				if (IsTerminal(this._lifecycle)) return;
				if (next == EventScheduleLifecycle.Started && this._lifecycle != EventScheduleLifecycle.Accepted) return;
				this._lifecycle = next;
				changed = this.LifecycleChanged;
				if (IsTerminal(next)) { terminalObservers = this._terminalObservers; this._terminalObservers = null; }
			}
			SafeInvoke(changed, next);
			SafeInvoke(terminalObservers, next);
		}

		private static bool IsTerminal(EventScheduleLifecycle lifecycle)
			=> lifecycle == EventScheduleLifecycle.Completed || lifecycle == EventScheduleLifecycle.Aborted || lifecycle == EventScheduleLifecycle.Rejected;

		private static void SafeInvoke(Action<EventScheduleLifecycle> callbacks, EventScheduleLifecycle lifecycle)
		{
			if (callbacks == null) return;
			foreach (Action<EventScheduleLifecycle> callback in callbacks.GetInvocationList())
			{
				try { callback(lifecycle); }
				catch { }
			}
		}
	}

	internal interface IEventScheduler
	{
		bool CheckAccess();
		EventScheduledOperation Post(Action drain);
	}

	internal sealed class EventEnqueueResult
	{
		internal EventEnqueueResult(EventEnqueueStatus status, long? sequence, EventEnqueueRejectionReason rejectionReason = EventEnqueueRejectionReason.None)
		{
			this.Status = status;
			this.Sequence = sequence;
			this.RejectionReason = rejectionReason;
		}
		internal EventEnqueueStatus Status { get; }
		internal long? Sequence { get; }
		internal EventEnqueueRejectionReason RejectionReason { get; }
	}

	internal sealed class SequencedEvent<T>
	{
		internal SequencedEvent(long sequence, T value) { this.Sequence = sequence; this.Value = value; }
		internal long Sequence { get; }
		internal T Value { get; }
	}

	internal sealed class EventIngress<T>
	{
		internal const int DefaultCapacity = 4096;

		private readonly object _gate = new object();
		private readonly Queue<SequencedEvent<T>> _queue = new Queue<SequencedEvent<T>>();
		private readonly IEventScheduler _scheduler;
		private readonly Action<SequencedEvent<T>> _consumer;
		private readonly Action<Exception, long> _faultSink;
		private readonly Action<SequencedEvent<T>> _accepted;
		private readonly int _capacity;
		private long _nextSequence;
		private long _nextGeneration;
		private long _scheduledGeneration;
		private long _operationGeneration;
		private long _handledTerminalGeneration;
		private long _postInProgressGeneration;
		private long _inlineDrainGeneration;
		private long? _lastRejectedSequence;
		private int _overflowCount;
		private bool _scheduled;
		private bool _accepting = true;
		private bool _shutdownRequested;
		private EventEnqueueRejectionReason _rejectionReason;
		private int _postingThreadId;
		private EventPumpState _state = EventPumpState.Ready;
		private EventScheduledOperation _operation;

		internal EventIngress(IEventScheduler scheduler, Action<SequencedEvent<T>> consumer, Action<Exception, long> faultSink, int capacity = DefaultCapacity, Action<SequencedEvent<T>> accepted = null)
		{
			if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
			this._scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
			this._consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
			this._faultSink = faultSink ?? ((_, __) => { });
			this._accepted = accepted;
			this._capacity = capacity;
		}

		internal int PostCount { get; private set; }
		internal EventPumpState State { get { lock (this._gate) return this._state; } }
		internal int PendingCount { get { lock (this._gate) return this._queue.Count; } }
		internal int Capacity => this._capacity;
		internal int OverflowCount { get { lock (this._gate) return this._overflowCount; } }
		internal long? LastRejectedSequence { get { lock (this._gate) return this._lastRejectedSequence; } }
		internal EventEnqueueRejectionReason StopReason { get { lock (this._gate) return this._rejectionReason; } }
		internal IReadOnlyList<long> PendingSequences { get { lock (this._gate) { var values = new List<long>(); foreach (var item in this._queue) values.Add(item.Sequence); return new ReadOnlyCollection<long>(values); } } }
		internal EventScheduledOperation CurrentOperation { get { lock (this._gate) return this._operation; } }

		internal EventEnqueueResult Enqueue(T value)
		{
			long sequence;
			long generation = 0;
			bool post = false;
			bool overflow = false;
			lock (this._gate)
			{
				sequence = ++this._nextSequence;
				if (!this._accepting) return new EventEnqueueResult(EventEnqueueStatus.Rejected, sequence, this._rejectionReason);
				if (this._queue.Count >= this._capacity)
				{
					this._accepting = false;
					this._rejectionReason = EventEnqueueRejectionReason.CapacityExceeded;
					this._overflowCount++;
					this._lastRejectedSequence = sequence;
					this._state = EventPumpState.Undrained;
					overflow = true;
				}
				else
				{
					var item = new SequencedEvent<T>(sequence, value);
					this._queue.Enqueue(item);
					this._accepted?.Invoke(item);
					if (!this._scheduled)
					{
						this._scheduled = true;
						generation = this._scheduledGeneration = ++this._nextGeneration;
						post = true;
					}
				}
			}

			if (overflow)
			{
				this.SafeFault(new InvalidOperationException("The event ingress reached its bounded capacity."), sequence);
				return new EventEnqueueResult(EventEnqueueStatus.Rejected, sequence, EventEnqueueRejectionReason.CapacityExceeded);
			}
			if (post) this.Schedule(sequence, generation);
			return new EventEnqueueResult(EventEnqueueStatus.Accepted, sequence);
		}

		internal void Shutdown()
		{
			EventScheduledOperation operation;
			lock (this._gate)
			{
				this._accepting = false;
				this._shutdownRequested = true;
				if (this._rejectionReason == EventEnqueueRejectionReason.None) this._rejectionReason = EventEnqueueRejectionReason.Shutdown;
				operation = this._operation;
				if (this._queue.Count == 0) this._state = this.GetStoppedEmptyState();
			}
			operation?.TryAbort();
		}

		private void Schedule(long sequence, long generation)
		{
			EventScheduledOperation operation;
			try
			{
				lock (this._gate)
				{
					this._postInProgressGeneration = generation;
					this._postingThreadId = Thread.CurrentThread.ManagedThreadId;
					this.PostCount++;
				}
				operation = this._scheduler.Post(() => this.Drain(generation));
			}
			catch (Exception ex)
			{
				this.FailPost(ex, sequence, generation);
				return;
			}
			finally
			{
				lock (this._gate)
				{
					if (this._postInProgressGeneration == generation) this._postInProgressGeneration = 0;
				}
			}

			if (operation == null || operation.InitialStatus == EventScheduleInitialStatus.Rejected)
			{
				this.FailPost(new InvalidOperationException("The event scheduler rejected the operation."), sequence, generation);
				return;
			}

			bool current;
			bool abort;
			bool inline;
			lock (this._gate)
			{
				current = this._scheduledGeneration == generation && this._operationGeneration < generation;
				if (current)
				{
					this._operation = operation;
					this._operationGeneration = generation;
				}
				abort = current && this._shutdownRequested;
				inline = this._inlineDrainGeneration == generation;
				if (inline && current) { this._state = EventPumpState.Faulted; this._scheduled = false; }
			}
			if (!current) return;
			operation.ObserveTerminal(lifecycle => this.OnOperationTerminal(generation, operation, lifecycle));
			if (abort) operation.TryAbort();
			if (inline) this.SafeFault(new InvalidOperationException("A strong event scheduler executed the drain inline."), sequence);
		}

		private void Drain(long generation)
		{
			lock (this._gate)
			{
				if (this._postInProgressGeneration == generation && this._postingThreadId == Thread.CurrentThread.ManagedThreadId)
				{
					this._inlineDrainGeneration = generation;
					return;
				}
				if (!this._scheduled || this._scheduledGeneration != generation) return;
				this._state = EventPumpState.Draining;
			}

			if (!this._scheduler.CheckAccess())
			{
				this.FailPost(new InvalidOperationException("The event drain did not run on its owner context."), 0, generation);
				return;
			}

			while (true)
			{
				SequencedEvent<T> item;
				lock (this._gate)
				{
					if (!this._scheduled || this._scheduledGeneration != generation) return;
					if (this._queue.Count == 0)
					{
						this._scheduled = false;
						this._state = this._accepting ? EventPumpState.Ready : this.GetStoppedEmptyState();
						return;
					}
					item = this._queue.Dequeue();
				}
				try { this._consumer(item); }
				catch (Exception ex) { this.SafeFault(ex, item.Sequence); }
			}
		}

		private void OnOperationTerminal(long generation, EventScheduledOperation operation, EventScheduleLifecycle lifecycle)
		{
			if (lifecycle != EventScheduleLifecycle.Aborted) return;
			long sequence;
			lock (this._gate)
			{
				if (this._operationGeneration != generation || !ReferenceEquals(this._operation, operation) || this._handledTerminalGeneration == generation) return;
				this._handledTerminalGeneration = generation;
				if (this._scheduledGeneration == generation) this._scheduled = false;
				sequence = this._queue.Count == 0 ? 0 : this._queue.Peek().Sequence;
				this._state = this._queue.Count == 0 ? EventPumpState.Faulted : EventPumpState.Undrained;
			}
			this.SafeFault(new InvalidOperationException("An accepted event operation was aborted."), sequence);
		}

		private void FailPost(Exception exception, long sequence, long generation)
		{
			lock (this._gate)
			{
				if (this._scheduledGeneration == generation) { this._scheduled = false; this._state = EventPumpState.Faulted; }
			}
			this.SafeFault(exception, sequence);
		}

		private void SafeFault(Exception exception, long sequence)
		{
			try { this._faultSink(exception, sequence); }
			catch { }
		}

		private EventPumpState GetStoppedEmptyState()
			=> this._rejectionReason == EventEnqueueRejectionReason.CapacityExceeded ? EventPumpState.Faulted : EventPumpState.Shutdown;
	}
}
