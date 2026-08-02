using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowsDesktop.Internal;
using Xunit;

namespace WindowsDesktop.Tests
{
	public class EventSchedulingTests
	{
		[Fact]
		public void BurstUsesOnePostAndDrainsInFifoOrder()
		{
			var scheduler = new FakeScheduler();
			var seen = new List<long>();
			var ingress = NewIngress<int>(scheduler, item => seen.Add(item.Sequence));
			for (var i = 0; i < 20; i++) ingress.Enqueue(i);

			Assert.Equal(1, ingress.PostCount);
			Assert.Equal(20, ingress.PendingCount);
			scheduler.Run();
			Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), seen);
			Assert.Equal(EventPumpState.Ready, ingress.State);
			Assert.Equal(EventScheduleLifecycle.Completed, scheduler.LastOperation.Lifecycle);
		}

		[Fact]
		public async Task MultipleProducersUseLockedIngressSequence()
		{
			var scheduler = new FakeScheduler();
			var accepted = new ConcurrentBag<long>();
			var seen = new List<long>();
			var ingress = NewIngress<int>(scheduler, item => seen.Add(item.Sequence));
			var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
			{
				for (var i = 0; i < 50; i++) accepted.Add(ingress.Enqueue((producer * 100) + i).Sequence.Value);
			})).ToArray();
			await Task.WhenAll(producers);

			Assert.Equal(200, accepted.Distinct().Count());
			Assert.Equal(1, ingress.PostCount);
			scheduler.AdoptCurrentThread();
			scheduler.Run();
			Assert.Equal(accepted.OrderBy(value => value), seen);
		}

		[Fact]
		public void ReentrantEnqueueAppendsToCurrentDrain()
		{
			var scheduler = new FakeScheduler();
			var seen = new List<int>();
			EventIngress<int> ingress = null;
			ingress = NewIngress<int>(scheduler, item =>
			{
				seen.Add(item.Value);
				if (item.Value == 1) ingress.Enqueue(3);
			});
			ingress.Enqueue(1);
			ingress.Enqueue(2);
			scheduler.Run();

			Assert.Equal(new[] { 1, 2, 3 }, seen);
			Assert.Equal(1, ingress.PostCount);
		}

		[Fact]
		public void RejectedPostLeavesDiagnosableQueue()
		{
			var scheduler = new FakeScheduler { Reject = true };
			var faults = new List<Exception>();
			var ingress = new EventIngress<int>(scheduler, _ => { }, (exception, __) => faults.Add(exception));
			var accepted = ingress.Enqueue(1);

			Assert.Equal(EventEnqueueStatus.Accepted, accepted.Status);
			Assert.Equal(EventPumpState.Faulted, ingress.State);
			Assert.Equal(new long[] { 1 }, ingress.PendingSequences);
			Assert.Single(faults);
		}

		[Fact]
		public void PostExceptionLeavesDiagnosableQueue()
		{
			var scheduler = new FakeScheduler { ThrowOnPost = true };
			var faults = new List<Exception>();
			var ingress = new EventIngress<int>(scheduler, _ => { }, (exception, __) => faults.Add(exception));
			ingress.Enqueue(1);

			Assert.Equal(EventPumpState.Faulted, ingress.State);
			Assert.Equal(new long[] { 1 }, ingress.PendingSequences);
			Assert.IsType<InvalidOperationException>(Assert.Single(faults));
		}

		[Fact]
		public void AcceptedOperationReportsStartedCompletedAndAbort()
		{
			var scheduler = new FakeScheduler();
			var ingress = NewIngress<int>(scheduler, _ => { });
			ingress.Enqueue(1);
			var lifecycle = new List<EventScheduleLifecycle>();
			scheduler.LastOperation.LifecycleChanged += lifecycle.Add;
			scheduler.Run();
			Assert.Equal(new[] { EventScheduleLifecycle.Started, EventScheduleLifecycle.Completed }, lifecycle);

			var secondScheduler = new FakeScheduler();
			var secondIngress = NewIngress<int>(secondScheduler, _ => { });
			secondIngress.Enqueue(1);
			secondScheduler.Abort();
			Assert.Equal(EventScheduleLifecycle.Aborted, secondScheduler.LastOperation.Lifecycle);
			Assert.Equal(EventPumpState.Undrained, secondIngress.State);
		}

		[Fact]
		public void RejectedOperationHasTerminalRejectedLifecycle()
		{
			var operation = new EventScheduledOperation(EventScheduleInitialStatus.Rejected);
			Assert.Equal(EventScheduleInitialStatus.Rejected, operation.InitialStatus);
			Assert.Equal(EventScheduleLifecycle.Rejected, operation.Lifecycle);
			Assert.False(operation.TryAbort());
		}

		[Fact]
		public void CurrentAcceptedOperationRemainsObservableUntilTerminal()
		{
			var scheduler = new FakeScheduler();
			var ingress = NewIngress<int>(scheduler, _ => { });
			ingress.Enqueue(1);
			Assert.Same(scheduler.LastOperation, ingress.CurrentOperation);
			Assert.Equal(EventScheduleLifecycle.Accepted, ingress.CurrentOperation.Lifecycle);
			scheduler.Run();
			Assert.Equal(EventScheduleLifecycle.Completed, ingress.CurrentOperation.Lifecycle);
		}

		[Fact]
		public void InlineSchedulerIsRejectedByStrongIngress()
		{
			var scheduler = new FakeScheduler { Inline = true };
			var seen = new List<int>();
			var ingress = NewIngress<int>(scheduler, item => seen.Add(item.Value));
			ingress.Enqueue(1);

			Assert.Empty(seen);
			Assert.Equal(EventPumpState.Faulted, ingress.State);
			Assert.Equal(1, ingress.PendingCount);
		}

		[Fact]
		public void ShutdownClassifiesAcceptedAndRejectedItems()
		{
			var scheduler = new FakeScheduler();
			var ingress = NewIngress<int>(scheduler, _ => { });
			var accepted = ingress.Enqueue(1);
			ingress.Shutdown();
			var rejected = ingress.Enqueue(2);

			Assert.Equal(EventEnqueueStatus.Accepted, accepted.Status);
			Assert.Equal(EventEnqueueStatus.Rejected, rejected.Status);
			Assert.Equal(new long[] { 1 }, ingress.PendingSequences);
			Assert.Equal(EventScheduleLifecycle.Aborted, scheduler.LastOperation.Lifecycle);
		}

		[Fact]
		public async Task ShutdownRaceNeverLosesClassification()
		{
			var scheduler = new FakeScheduler();
			var ingress = NewIngress<int>(scheduler, _ => { });
			var results = new ConcurrentBag<EventEnqueueResult>();
			var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
			{
				for (var i = 0; i < 100; i++) results.Add(ingress.Enqueue((worker * 1000) + i));
			})).ToArray();
			ingress.Shutdown();
			await Task.WhenAll(workers);

			Assert.Equal(800, results.Count);
			Assert.All(results, result => Assert.True(result.Status == EventEnqueueStatus.Accepted || result.Status == EventEnqueueStatus.Rejected));
			Assert.Equal(results.Count(result => result.Status == EventEnqueueStatus.Accepted), ingress.PendingCount);
		}

		[Fact]
		public void ConsumerFailureDoesNotDiscardFollowingDto()
		{
			var scheduler = new FakeScheduler();
			var seen = new List<int>();
			var faults = new List<Exception>();
			var ingress = new EventIngress<int>(scheduler, item =>
			{
				if (item.Value == 1) throw new InvalidOperationException("synthetic");
				seen.Add(item.Value);
			}, (exception, __) => faults.Add(exception));
			ingress.Enqueue(1);
			ingress.Enqueue(2);
			scheduler.Run();

			Assert.Equal(new[] { 2 }, seen);
			Assert.Single(faults);
			Assert.Equal(0, ingress.PendingCount);
		}

		[Fact]
		public void ThrowingFaultSinkCannotStopTheDrain()
		{
			var scheduler = new FakeScheduler();
			var seen = new List<int>();
			var ingress = new EventIngress<int>(scheduler, item =>
			{
				if (item.Value == 1) throw new InvalidOperationException("synthetic consumer");
				seen.Add(item.Value);
			}, (_, __) => throw new InvalidOperationException("synthetic fault sink"));
			ingress.Enqueue(1);
			ingress.Enqueue(2);
			scheduler.Run();
			Assert.Equal(new[] { 2 }, seen);
			Assert.Equal(0, ingress.PendingCount);
			Assert.Equal(EventPumpState.Ready, ingress.State);
		}

		[Fact]
		public void BoundedIngressFailsClosedWithoutUnboundedGrowth()
		{
			var scheduler = new FakeScheduler();
			var faults = 0;
			var seen = new List<int>();
			var ingress = new EventIngress<int>(scheduler, item => seen.Add(item.Value), (_, __) => faults++, 2);
			Assert.Equal(EventEnqueueStatus.Accepted, ingress.Enqueue(1).Status);
			Assert.Equal(EventEnqueueStatus.Accepted, ingress.Enqueue(2).Status);
			var overflow = ingress.Enqueue(3);
			var afterOverflow = ingress.Enqueue(4);
			Assert.Equal(EventEnqueueRejectionReason.CapacityExceeded, overflow.RejectionReason);
			Assert.Equal(EventEnqueueRejectionReason.CapacityExceeded, afterOverflow.RejectionReason);
			Assert.Equal(2, ingress.PendingCount);
			Assert.Equal(1, ingress.OverflowCount);
			Assert.Equal(3, ingress.LastRejectedSequence);
			Assert.Equal(1, faults);

			scheduler.Run();
			Assert.Equal(new[] { 1, 2 }, seen);
			Assert.Equal(0, ingress.PendingCount);
			Assert.Equal(EventPumpState.Faulted, ingress.State);
			Assert.Equal(EventEnqueueRejectionReason.CapacityExceeded, ingress.StopReason);
			Assert.Equal(EventEnqueueRejectionReason.CapacityExceeded, ingress.Enqueue(5).RejectionReason);

			ingress.Shutdown();
			Assert.Equal(EventPumpState.Faulted, ingress.State);
			Assert.Equal(EventEnqueueRejectionReason.CapacityExceeded, ingress.StopReason);
		}

		[Fact]
		public void TerminalStateBeforeObserverRegistrationIsReplayedExactlyOnce()
		{
			var scheduler = new PreAbortedScheduler();
			var faults = 0;
			var ingress = new EventIngress<int>(scheduler, _ => { }, (_, __) => faults++);
			ingress.Enqueue(1);
			Assert.Equal(EventScheduleLifecycle.Aborted, scheduler.Operation.Lifecycle);
			Assert.Equal(EventPumpState.Undrained, ingress.State);
			Assert.Equal(1, faults);
		}

		[Fact]
		public void LateReturnFromOlderPostCannotReplaceNewerCurrentOperation()
		{
			var scheduler = new RacingScheduler();
			var ingress = new EventIngress<int>(scheduler, _ => { }, (_, __) => { });
			Exception workerError = null;
			var worker = new Thread(() =>
			{
				try { ingress.Enqueue(1); }
				catch (Exception ex) { workerError = ex; }
			});
			worker.Start();
			Assert.True(scheduler.FirstDrainCompleted.Wait(TimeSpan.FromSeconds(10)), "First drain did not complete.");
			ingress.Enqueue(2);
			var second = scheduler.SecondOperation;
			Assert.Same(second, ingress.CurrentOperation);
			scheduler.ReleaseFirstPostReturn.Set();
			Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "First Post did not return.");
			Assert.Null(workerError);
			Assert.Same(second, ingress.CurrentOperation);
			ingress.Shutdown();
			Assert.Equal(EventScheduleLifecycle.Aborted, second.Lifecycle);
			Assert.Equal(EventPumpState.Undrained, ingress.State);
		}

		[Fact]
		public void CallbackMaterializerCopiesManagedDtoAndNormalizesNullString()
		{
			var id = Guid.NewGuid();
			var raw = new CallbackDesktop(id);
			var materializer = new VirtualDesktopCallbackMaterializer(typeof(ICallbackDesktop));
			var dto = materializer.Property(VirtualDesktopCallbackKind.Renamed, raw, null);
			raw.Dispose();

			Assert.Equal(id, dto.DesktopId);
			Assert.Equal(string.Empty, dto.Value);
			Assert.Equal(1, raw.IdCalls);
			Assert.All(dto.GetType().GetProperties(), property => Assert.DoesNotContain("Object", property.PropertyType.Name));
		}

		[Fact]
		public void CallbackMaterializationFailureDoesNotCreateDto()
		{
			var raw = new CallbackDesktop(Guid.NewGuid()) { IdError = new COMException("synthetic", 17) };
			var materializer = new VirtualDesktopCallbackMaterializer(typeof(ICallbackDesktop));
			var exception = Assert.Throws<COMException>(() => materializer.One(VirtualDesktopCallbackKind.Created, raw));
			Assert.Equal(17, exception.HResult);
		}

		[Fact]
		public void CallbackDtoContainsOnlyImmutableManagedData()
		{
			var dto = new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Moved, Guid.NewGuid(), Guid.NewGuid(), 3, 4, "synthetic");
			var forbidden = new[] { typeof(Delegate), typeof(Exception), typeof(System.Reflection.MemberInfo) };
			foreach (var property in dto.GetType().GetProperties())
			{
				Assert.DoesNotContain(forbidden, type => type.IsAssignableFrom(property.PropertyType));
				Assert.False(property.PropertyType == typeof(object));
			}
		}

		[Fact]
		public void MaterializerPreservesAllCallbackScalarShapes()
		{
			var first = Guid.NewGuid();
			var second = Guid.NewGuid();
			var materializer = new VirtualDesktopCallbackMaterializer(typeof(ICallbackDesktop));
			var one = materializer.One(VirtualDesktopCallbackKind.Created, new CallbackDesktop(first));
			var two = materializer.Two(VirtualDesktopCallbackKind.CurrentChanged, new CallbackDesktop(first), new CallbackDesktop(second));
			var move = materializer.Move(new CallbackDesktop(first), 5, 2);
			var view = materializer.ApplicationViewChanged();

			Assert.Equal(first, one.DesktopId);
			Assert.Equal(second, two.RelatedDesktopId);
			Assert.Equal(5, move.OldIndex);
			Assert.Equal(2, move.NewIndex);
			Assert.Null(view.DesktopId);
		}

		[Fact]
		public void MirrorTransitionPublishesFixedOldAndNewValuesInOrder()
		{
			var value = "A";
			var phases = new List<string>();
			var transition = new VirtualDesktopMirrorTransition(() => value, next => value = next);
			var applied = transition.Apply("B",
				(oldValue, newValue) => phases.Add("changing:" + value + ":" + oldValue + ":" + newValue),
				(oldValue, newValue) => phases.Add("changed:" + value + ":" + oldValue + ":" + newValue),
				(oldValue, newValue) => phases.Add("public:" + oldValue + ":" + newValue));

			Assert.True(applied);
			Assert.Equal("B", value);
			Assert.Equal(new[] { "changing:A:A:B", "changed:B:A:B", "public:A:B" }, phases);
		}

		[Fact]
		public void SameValueDoesNotPublishAndRapidTransitionsDoNotAliasValues()
		{
			var value = "A";
			var published = new List<string>();
			var transition = new VirtualDesktopMirrorTransition(() => value, next => value = next);
			Assert.False(transition.Apply("A", (_, __) => published.Add("changing"), null, null));
			transition.Apply("B", null, null, (oldValue, newValue) => published.Add(oldValue + "->" + newValue));
			transition.Apply("C", null, null, (oldValue, newValue) => published.Add(oldValue + "->" + newValue));

			Assert.Equal(new[] { "A->B", "B->C" }, published);
		}

		[Fact]
		public void StrongSetterRequiresOwnerAndCommitsOnlyAfterComSuccess()
		{
			var scheduler = new FakeScheduler();
			var provider = new VirtualDesktopProvider();
			var pipeline = new VirtualDesktopEventPipeline(provider, scheduler);
			var calls = 0;
			var mirror = "A";
			pipeline.ExecuteSetter(() => { calls++; Assert.Equal("A", mirror); }, () => mirror = "B");
			Assert.Equal(1, calls);
			Assert.Equal("B", mirror);

			Exception offOwnerError = null;
			var thread = new Thread(() =>
			{
				try { pipeline.ExecuteSetter(() => calls++, () => mirror = "C"); }
				catch (Exception ex) { offOwnerError = ex; }
			});
			thread.Start();
			Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Off-owner setter test thread hung.");
			Assert.IsType<InvalidOperationException>(offOwnerError);
			Assert.Equal(1, calls);
		}

		[Fact]
		public void ComFailureDoesNotCommitMirror()
		{
			var pipeline = new VirtualDesktopEventPipeline(new VirtualDesktopProvider(), new FakeScheduler());
			var mirror = "A";
			Assert.Throws<COMException>(() => pipeline.ExecuteSetter(() => throw new COMException("synthetic", 19), () => mirror = "B"));
			Assert.Equal("A", mirror);
		}

		[Fact]
		public void LegacySetterRunsInlineAndCommitsBeforeReturn()
		{
			var pipeline = new VirtualDesktopEventPipeline(new VirtualDesktopProvider(), null);
			var phases = new List<string>();
			pipeline.ExecuteSetter(() => phases.Add("com"), () => phases.Add("commit"));
			phases.Add("return");
			Assert.Equal(new[] { "com", "commit", "return" }, phases);
		}

		[Fact]
		public void StrongAcceptPostsWithoutInlinePublication()
		{
			var scheduler = new FakeScheduler();
			var provider = new VirtualDesktopProvider();
			var pipeline = new VirtualDesktopEventPipeline(provider, scheduler);
			pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged));
			Assert.Equal(1, pipeline.PendingCount);
			Assert.Equal(1, pipeline.PostCount);
			Assert.Equal(EventScheduleLifecycle.Accepted, scheduler.LastOperation.Lifecycle);
			pipeline.Shutdown();
		}

		[Fact]
		public void FaultDtoDoesNotContainMessageOrUserValues()
		{
			var fault = new VirtualDesktopProviderFault(VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.WallpaperChanged, Guid.NewGuid(), typeof(COMException).FullName, 23, 9);
			Assert.Equal(typeof(COMException).FullName, fault.ExceptionType);
			Assert.DoesNotContain(fault.GetType().GetProperties(), property => property.Name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0 || property.Name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0 || property.Name.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		[Fact]
		public void StrongSubscriberFailureIsFaultedAndFaultSubscriberCannotBreakPipeline()
		{
			var provider = new VirtualDesktopProvider();
			var scheduler = new FakeScheduler();
			var pipeline = new VirtualDesktopEventPipeline(provider, scheduler);
			var faults = new List<VirtualDesktopProviderFault>();
			var reentrantComCalls = 0;
			provider.EventDispatchFaulted += (_, fault) =>
			{
				faults.Add(fault);
				Assert.Throws<InvalidOperationException>(() => pipeline.ExecuteSetter(() => reentrantComCalls++, () => { }));
				throw new InvalidOperationException("fault subscriber");
			};
			var collector = new ExceptionCollector();
			pipeline.InvokeSubscriber(new Action(() => { }), () => throw new InvalidOperationException("subscriber"), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.Renamed, Guid.NewGuid(), 7, collector);

			Assert.Single(faults);
			Assert.Equal(typeof(InvalidOperationException).FullName, faults[0].ExceptionType);
			Assert.Equal(7, faults[0].Sequence);
			Assert.Equal(0, reentrantComCalls);
		}

		[Fact]
		public void LegacySubscriberFailuresCompletePhaseThenRethrowFirst()
		{
			var pipeline = new VirtualDesktopEventPipeline(new VirtualDesktopProvider(), null);
			var collector = new ExceptionCollector();
			var seen = new List<int>();
			var first = new InvalidOperationException("first");
			pipeline.InvokeSubscriber(new Action(() => { }), () => throw first, VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.Renamed, null, 0, collector);
			pipeline.InvokeSubscriber(new Action(() => { }), () => seen.Add(2), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.Renamed, null, 0, collector);

			Assert.Equal(new[] { 2 }, seen);
			Assert.Same(first, Assert.Throws<InvalidOperationException>(collector.ThrowFirst));
		}

		[Fact]
		public void StrongPublicCallbackRejectsReentrantSetterBeforeCom()
		{
			var scheduler = new FakeScheduler();
			var pipeline = new VirtualDesktopEventPipeline(new VirtualDesktopProvider(), scheduler);
			var calls = 0;
			var collector = new ExceptionCollector();
			pipeline.InvokeSubscriber(new Action(() => { }), () => Assert.Throws<InvalidOperationException>(() => pipeline.ExecuteSetter(() => calls++, () => { })), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.Renamed, null, 0, collector);
			Assert.Equal(0, calls);
		}

		private static EventIngress<T> NewIngress<T>(FakeScheduler scheduler, Action<SequencedEvent<T>> consumer)
			=> new EventIngress<T>(scheduler, consumer, (_, __) => { });

		[Guid("13e055d2-2657-4836-95ce-ecc9f08fd92d")]
		private interface ICallbackDesktop
		{
			Guid GetID();
		}

		private sealed class CallbackDesktop : ICallbackDesktop, IDisposable
		{
			private readonly Guid _id;
			private bool _disposed;
			internal CallbackDesktop(Guid id) => this._id = id;
			internal int IdCalls { get; private set; }
			internal Exception IdError { get; set; }
			public Guid GetID() { if (this._disposed) throw new ObjectDisposedException(nameof(CallbackDesktop)); this.IdCalls++; if (this.IdError != null) throw this.IdError; return this._id; }
			public void Dispose() => this._disposed = true;
		}

		private sealed class FakeScheduler : IEventScheduler
		{
			private int _ownerThreadId = Environment.CurrentManagedThreadId;
			private Action _drain;
			internal bool Reject { get; set; }
			internal bool ThrowOnPost { get; set; }
			internal bool Inline { get; set; }
			internal EventScheduledOperation LastOperation { get; private set; }
			public bool CheckAccess() => Environment.CurrentManagedThreadId == this._ownerThreadId;
			public EventScheduledOperation Post(Action drain)
			{
				if (this.ThrowOnPost) throw new InvalidOperationException("synthetic");
				this.LastOperation = new EventScheduledOperation(this.Reject ? EventScheduleInitialStatus.Rejected : EventScheduleInitialStatus.Accepted);
				if (this.Reject) return this.LastOperation;
				this._drain = drain;
				this.LastOperation.SetAbort(() => { this._drain = null; this.LastOperation.MarkAborted(); return true; });
				if (this.Inline) { this.LastOperation.MarkStarted(); drain(); this.LastOperation.MarkCompleted(); }
				return this.LastOperation;
			}
			internal void Run() { var drain = this._drain; this._drain = null; this.LastOperation.MarkStarted(); drain(); this.LastOperation.MarkCompleted(); }
			internal void Abort() => this.LastOperation.TryAbort();
			internal void AdoptCurrentThread() => this._ownerThreadId = Environment.CurrentManagedThreadId;
		}

		private sealed class PreAbortedScheduler : IEventScheduler
		{
			internal EventScheduledOperation Operation { get; private set; }
			public bool CheckAccess() => true;
			public EventScheduledOperation Post(Action drain)
			{
				this.Operation = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
				this.Operation.MarkAborted();
				return this.Operation;
			}
		}

		private sealed class RacingScheduler : IEventScheduler
		{
			private int _postCount;
			internal ManualResetEventSlim FirstDrainCompleted { get; } = new ManualResetEventSlim();
			internal ManualResetEventSlim ReleaseFirstPostReturn { get; } = new ManualResetEventSlim();
			internal EventScheduledOperation SecondOperation { get; private set; }
			public bool CheckAccess() => true;
			public EventScheduledOperation Post(Action drain)
			{
				var post = Interlocked.Increment(ref this._postCount);
				var operation = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
				if (post == 1)
				{
					var thread = new Thread(() =>
					{
						operation.MarkStarted();
						drain();
						operation.MarkCompleted();
						this.FirstDrainCompleted.Set();
					});
					thread.Start();
					Assert.True(this.FirstDrainCompleted.Wait(TimeSpan.FromSeconds(10)), "First drain thread hung.");
					Assert.True(this.ReleaseFirstPostReturn.Wait(TimeSpan.FromSeconds(10)), "First Post release was not signaled.");
					return operation;
				}
				this.SecondOperation = operation;
				operation.SetAbort(() => { operation.MarkAborted(); return true; });
				return operation;
			}
		}
	}
}
