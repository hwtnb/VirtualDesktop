using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WindowsDesktop.Internal;
using WindowsDesktop.Interop;
using Xunit;

namespace WindowsDesktop.Tests
{
	public class ProviderReconciliationTests
	{
		[Fact]
		public async Task ExplicitRequestPublishesStableBatchAndMonotonicRevisions()
		{
			var id = Guid.NewGuid();
			var capture = new FakeCapture(Batch(id, "A", "wall-A"), Batch(id, "B", "wall-B"));
			var provider = Provider(capture);
			var published = new List<VirtualDesktopStableBatch>();
			provider.StableBatchPublished += (_, batch) => published.Add(batch);

			var first = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var second = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, first.Status);
			Assert.Equal(new long[] { 1, 2 }, published.Select(x => x.SnapshotRevision));
			Assert.All(published, batch => Assert.Equal(1, batch.ProviderEpoch));
			Assert.Equal("B", second.Batch.Desktops[0].Name);
			Assert.Equal(2, capture.Calls);
		}

		[Fact]
		public async Task RequestBeforeRuntimeInitializationIsTypedUnavailable()
		{
			var provider = new VirtualDesktopProvider(new FakeRuntime());

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.Equal(VirtualDesktopReconciliationStatus.Unavailable, result.Status);
			Assert.Equal(VirtualDesktopProviderFailureCategory.ReconciliationUnavailable, result.FailureCategory);
		}

		[Fact]
		public async Task MultipleWaitersCoalesceIntoOneProviderOwnedCapture()
		{
			var scheduler = new ManualEventScheduler();
			var capture = new FakeCapture(Batch(Guid.NewGuid(), "A", "wall"));
			var provider = Provider(capture, scheduler);
			var first = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var second = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal(1, scheduler.PendingCount);
			Assert.Equal(0, capture.Calls);
			scheduler.RunAll();

			Assert.Equal(1, capture.Calls);
			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, (await first).Status);
			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, (await second).Status);
		}

		[Fact]
		public void StrongInlineSchedulerCannotRunReconciliationOnPostingStack()
		{
			var delays = new ManualDelayScheduler();
			var capture = new FakeCapture(Batch(Guid.NewGuid(), "A", "wall"));
			var provider = Provider(new FakeRuntime(), capture, new InlineEventScheduler(), delays);
			var faults = new List<VirtualDesktopProviderFault>();
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);

			var request = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.False(request.IsCompleted);
			Assert.Equal(0, capture.Calls);
			Assert.Equal(1, delays.ActiveCount);
			Assert.Contains(faults, x => x.FailureCategory == VirtualDesktopProviderFailureCategory.Scheduler);
		}

		[Fact]
		public async Task StructuralFailurePublishesNothingAndRetriesWithoutDestroyingLkg()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var capture = new FakeCapture(Batch(id, "A", "wall"), StructuralFailure(), Batch(id, "A", "wall"));
			var provider = Provider(capture, null, delays);
			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, (await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization)).Status);
			var published = 0;
			provider.StableBatchPublished += (_, __) => published++;
			var request = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal(0, published);
			Assert.False(request.IsCompleted);
			Assert.Single(delays.Delays);
			delays.RunNext();

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, (await request).Status);
			Assert.Equal(1, published);
			Assert.Equal("A", provider.LastStableBatch.Desktops[0].Name);
		}

		[Fact]
		public async Task RetryExhaustionCompletesWaiterUnavailableAndReportsPrivacySafeFault()
		{
			var delays = new ManualDelayScheduler();
			var capture = new FakeCapture(StructuralFailure());
			var provider = Provider(capture, null, delays, new ReconciliationRetryPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4)));
			var faults = new List<VirtualDesktopProviderFault>();
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);
			var request = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			delays.RunNext();
			delays.RunNext();

			var result = await request;
			Assert.Equal(VirtualDesktopReconciliationStatus.Unavailable, result.Status);
			Assert.Equal(VirtualDesktopProviderFailureCategory.StructuralSnapshot, result.FailureCategory);
			Assert.Contains(faults, x => x.FailureCategory == VirtualDesktopProviderFailureCategory.RetryExhausted);
			Assert.All(faults, x => Assert.DoesNotContain("synthetic-value", x.ExceptionType ?? string.Empty));
			Assert.Equal(new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4) }, delays.Delays);
		}

		[Fact]
		public async Task IngressDuringCaptureDiscardsStaleCaptureAndCannotOverwriteNewerBatch()
		{
			var id = Guid.NewGuid();
			VirtualDesktopProvider provider = null;
			var capture = new CallbackCapture(
				() => { provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, id, value: "new")); return Batch(id, "old", "wall"); },
				() => Batch(id, "new", "wall"));
			provider = Provider(capture);
			var published = new List<string>();
			provider.StableBatchPublished += (_, batch) => published.Add(batch.Desktops[0].Name);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, result.Status);
			Assert.Equal(new[] { "new" }, published);
			Assert.Equal(2, capture.Calls);
			Assert.Equal(1, provider.SnapshotRevision);
		}

		[Fact]
		public async Task CallbackStormCoalescesIngressPostAndRawCapture()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(Batch(id, "A", "wall"), Batch(id, "final", "wall"));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, id, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			for (var i = 0; i < 40; i++) provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, id, value: "candidate-" + i));
			Assert.Equal(1, provider.EventPipeline.PostCount);
			scheduler.RunAll();

			Assert.Equal(2, capture.Calls);
			Assert.Equal("final", provider.LastStableBatch.Desktops[0].Name);
			Assert.Equal(0, provider.EventPipeline.PendingCount);
		}

		[Theory]
		[InlineData(VirtualDesktopReadStatus.Failed)]
		[InlineData(VirtualDesktopReadStatus.NotAttempted)]
		public async Task KnownReadFailureKeepsLastKnownGood(VirtualDesktopReadStatus status)
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "LKG", "wall"), Batch(id, Read(status), Success("wall"))), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal("LKG", result.Batch.Desktops[0].Name);
			Assert.Equal(status, result.Batch.Desktops[0].NameReadStatus);
			Assert.Single(delays.Delays);
		}

		[Fact]
		public async Task UnsupportedKeepsAuthorityValueAndDoesNotRetry()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "LKG", "wall"), Batch(id, Unsupported(), Unsupported())), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal("LKG", result.Batch.Desktops[0].Name);
			Assert.Equal(VirtualDesktopReadStatus.Unsupported, result.Batch.Desktops[0].NameReadStatus);
			Assert.Empty(delays.Delays);
			Assert.False(provider.IsDirty);
		}

		[Fact]
		public async Task NewFailedPropertyRemainsUnknownInsteadOfBorrowingByIndex()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, Failed(), Failed())), null, delays);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.Null(result.Batch.Desktops[0].Name);
			Assert.Null(result.Batch.Desktops[0].WallpaperPath);
			Assert.Equal(VirtualDesktopReadStatus.Failed, result.Batch.Desktops[0].NameReadStatus);
		}

		[Fact]
		public async Task KnownNonEmptyRequiresTwoAcceptedEmptySnapshots()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(id, "", "wall"), Batch(id, "", "wall")), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			var firstEmpty = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			Assert.Equal("A", firstEmpty.Batch.Desktops[0].Name);
			Assert.Equal(1, provider.EmptyCandidateCount);
			delays.RunNext();

			Assert.Equal(string.Empty, provider.LastStableBatch.Desktops[0].Name);
			Assert.Equal(0, provider.EmptyCandidateCount);
		}

		[Fact]
		public async Task EmptyCandidateSurvivesFailedReadAndSecondEmptyConfirms()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(id, "", "wall"), Batch(id, Failed(), Success("wall")), Batch(id, "", "wall")), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			delays.RunNext();
			Assert.Equal("A", provider.LastStableBatch.Desktops[0].Name);
			Assert.Equal(1, provider.EmptyCandidateCount);
			delays.RunNext();

			Assert.Equal(string.Empty, provider.LastStableBatch.Desktops[0].Name);
		}

		[Fact]
		public async Task NonEmptyAfterNotAttemptedClearsEmptyCandidate()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(id, "", "wall"), Batch(id, NotAttempted(), Success("wall")), Batch(id, "B", "wall")), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			delays.RunNext();
			delays.RunNext();

			Assert.Equal("B", provider.LastStableBatch.Desktops[0].Name);
			Assert.Equal(0, provider.EmptyCandidateCount);
		}

		[Fact]
		public async Task UnrelatedCallbackDoesNotAdvanceEmptyConfirmation()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(id, "", "wall"), Batch(id, "", "wall")), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			Assert.Equal(1, provider.EmptyCandidateCount);

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged));

			Assert.Equal(1, provider.EmptyCandidateCount);
			Assert.Equal("A", provider.LastStableBatch.Desktops[0].Name);
			delays.RunNext();
			Assert.Equal(string.Empty, provider.LastStableBatch.Desktops[0].Name);
		}

		[Fact]
		public async Task ResetClearsCandidateAndSupersedesOldWaiter()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var scheduler = new ManualEventScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(id, "", "wall"), Batch(id, "new", "wall")), scheduler, delays);
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var empty = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation); scheduler.RunAll(); await empty;
			Assert.Equal(1, provider.EmptyCandidateCount);
			var waiter = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			var epoch = provider.ResetRuntime();

			Assert.Equal(2, epoch);
			Assert.Equal(0, provider.EmptyCandidateCount);
			Assert.Equal(VirtualDesktopReconciliationStatus.SupersededByReset, (await waiter).Status);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task LocalExplicitEmptyCommitsImmediatelyCreatesPendingAndRequestsSnapshot(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(PropertyBatch(id, property, "A"), PropertyBatch(id, property, ""));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			SetProperty(desktop, property, string.Empty);

			Assert.Equal(string.Empty, PropertyValue(desktop, property));
			Assert.Equal(1, provider.PendingWriteCount);
			Assert.Equal(1, scheduler.PendingCount);
			scheduler.RunAll();
			Assert.Equal(string.Empty, PropertyValue(provider.LastStableBatch.Desktops[0], property));
			Assert.Equal(0, provider.PendingWriteCount);
		}

		[Fact]
		public async Task NewerPendingWriteIsNotClearedByOldAcknowledgement()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall"), Batch(id, "C", "wall")), scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			desktop.Name = "B";
			desktop.Name = "C";
			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, id, value: "B"));
			scheduler.RunAll();

			Assert.Equal("C", desktop.Name);
			Assert.Equal(0, provider.PendingWriteCount);
			Assert.Equal("C", provider.LastStableBatch.Desktops[0].Name);
		}

		[Fact]
		public async Task MatchingAcknowledgementClearsOnlyCurrentPendingGeneration()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall"), Batch(id, "B", "wall")), scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			desktop.Name = "B";
			Assert.Equal(1, provider.PendingWriteCount);

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, id, value: "B"));
			scheduler.RunAll();

			Assert.Equal(0, provider.PendingWriteCount);
			Assert.Equal("B", provider.LastStableBatch.Desktops[0].Name);
		}

		[Fact]
		public void ComFailureCreatesNoPendingAndLeavesMirrorUnchanged()
		{
			var id = Guid.NewGuid();
			var runtime = new FakeRuntime { SetNameError = new InvalidOperationException("synthetic") };
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall")), null, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "wall");

			Assert.Throws<InvalidOperationException>(() => desktop.Name = "B");

			Assert.Equal("A", desktop.Name);
			Assert.Equal(0, provider.PendingWriteCount);
		}

		[Theory]
		[InlineData((int)VirtualDesktopCallbackKind.Created)]
		[InlineData((int)VirtualDesktopCallbackKind.Destroyed)]
		[InlineData((int)VirtualDesktopCallbackKind.Moved)]
		public async Task TopologyCallbacksRequestFullReconciliation(int kindValue)
		{
			var kind = (VirtualDesktopCallbackKind)kindValue;
			var id = Guid.NewGuid();
			var fallback = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(Batch(id, "A", "wall"), Batch(id, "A", "wall"));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, id, "A", "wall");
			Desktop(provider, runtime, fallback, "F", "wall-F");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var dto = kind == VirtualDesktopCallbackKind.Moved
				? new VirtualDesktopCallbackDto(kind, id, oldIndex: 0, newIndex: 1)
				: kind == VirtualDesktopCallbackKind.Destroyed
					? new VirtualDesktopCallbackDto(kind, id, fallback)
					: new VirtualDesktopCallbackDto(kind, id);

			provider.EventPipeline.Accept(dto);
			scheduler.RunAll();

			Assert.Equal(2, capture.Calls);
			Assert.Equal(VirtualDesktopStableReason.TopologyChanged, provider.LastStableBatch.Reason);
		}

		[Fact]
		public async Task CleanKnownCurrentChangedPublishesCurrentOnlyAgainstStableRevision()
		{
			var first = Guid.NewGuid();
			var second = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(new[] { Entry(first, 0, "A", "wall-A"), Entry(second, 1, "B", "wall-B") }, first)), scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, first, "A", "wall-A");
			Desktop(provider, runtime, second, "B", "wall-B");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			VirtualDesktopCurrentTransition observed = null;
			provider.CurrentTransitioned += (_, transition) => observed = transition;

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, first, second));
			scheduler.RunAll();

			Assert.NotNull(observed);
			Assert.Equal(second, observed.CurrentDesktopId);
			Assert.Equal(provider.SnapshotRevision, observed.BaseSnapshotRevision);
			Assert.Equal(1, provider.SnapshotRevision);
		}

		[Fact]
		public async Task UnknownCurrentChangedEscalatesWithoutIndexGuessing()
		{
			var known = Guid.NewGuid();
			var unknown = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(Batch(known, "A", "wall"), Batch(known, "A", "wall"));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, known, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var transitions = 0;
			provider.CurrentTransitioned += (_, __) => transitions++;

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, known, unknown));
			scheduler.RunAll();

			Assert.Equal(0, transitions);
			Assert.Equal(2, capture.Calls);
		}

		[Fact]
		public async Task CreatedThenCurrentChangedNewIdUsesOneFullReconciliation()
		{
			var oldId = Guid.NewGuid();
			var newId = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(
				Batch(oldId, "old", "wall-old"),
				Batch(new[] { Entry(oldId, 0, "old", "wall-old"), Entry(newId, 1, "new", "wall-new") }, newId));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, oldId, "old", "wall-old");
			Desktop(provider, runtime, newId, "new", "wall-new");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var currentOnly = 0;
			provider.CurrentTransitioned += (_, __) => currentOnly++;

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Created, newId));
			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, oldId, newId));
			scheduler.RunAll();

			Assert.Equal(0, currentOnly);
			Assert.Equal(2, capture.Calls);
			Assert.Equal(newId, provider.LastStableBatch.CurrentDesktopId);
			Assert.Equal(new[] { oldId, newId }, provider.LastStableBatch.Desktops.Select(x => x.Id));
		}

		[Fact]
		public async Task CurrentChangedThenDestroyedDoesNotPublishTransitionPastTopologyBarrier()
		{
			var removed = Guid.NewGuid();
			var fallback = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(
				Batch(new[] { Entry(removed, 0, "removed", "wall-r"), Entry(fallback, 1, "fallback", "wall-f") }, removed),
				Batch(new[] { Entry(fallback, 0, "fallback", "wall-f") }, fallback));
			var provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, removed, "removed", "wall-r");
			Desktop(provider, runtime, fallback, "fallback", "wall-f");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var transitions = 0;
			provider.CurrentTransitioned += (_, __) => transitions++;

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, fallback, removed));
			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Destroyed, removed, fallback));
			scheduler.RunAll();

			Assert.Equal(0, transitions);
			Assert.Equal(fallback, provider.LastStableBatch.CurrentDesktopId);
			Assert.Single(provider.LastStableBatch.Desktops);
		}

		[Fact]
		public async Task CurrentChangedDuringSnapshotDiscardsCaptureAndEscalates()
		{
			var first = Guid.NewGuid();
			var second = Guid.NewGuid();
			VirtualDesktopProvider provider = null;
			var capture = new CallbackCapture(
				() => { provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, first, second)); return Batch(new[] { Entry(first, 0, "A", "wa"), Entry(second, 1, "B", "wb") }, first); },
				() => Batch(new[] { Entry(first, 0, "A", "wa"), Entry(second, 1, "B", "wb") }, second));
			provider = Provider(capture);
			var transitions = 0;
			provider.CurrentTransitioned += (_, __) => transitions++;

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.Equal(0, transitions);
			Assert.Equal(second, result.Batch.CurrentDesktopId);
			Assert.Equal(2, capture.Calls);
		}

		[Fact]
		public async Task CurrentReadFailureStillPublishesCompleteTopologyWithLkgCurrent()
		{
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(Batch(id, "A", "wall"), Batch(new[] { Entry(id, 0, "A", "wall") }, FailedGuid())), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Single(result.Batch.Desktops);
			Assert.Equal(id, result.Batch.CurrentDesktopId);
			Assert.Equal(VirtualDesktopReadStatus.Failed, result.Batch.CurrentDesktopReadStatus);
			Assert.Single(delays.Delays);
		}

		[Fact]
		public async Task CancellationResetAndShutdownHaveDistinctTerminalResults()
		{
			var scheduler = new ManualEventScheduler();
			var provider = Provider(new FakeCapture(Batch(Guid.NewGuid(), "A", "wall")), scheduler);
			var cancellation = new CancellationTokenSource();
			var cancelled = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation, cancellation.Token);
			cancellation.Cancel();
			Assert.Equal(VirtualDesktopReconciliationStatus.Cancelled, (await cancelled).Status);

			var reset = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			provider.ResetRuntime();
			Assert.Equal(VirtualDesktopReconciliationStatus.SupersededByReset, (await reset).Status);

			var shutdown = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			provider.Dispose();
			Assert.Equal(VirtualDesktopReconciliationStatus.ShuttingDown, (await shutdown).Status);
			Assert.Equal(VirtualDesktopReconciliationStatus.ShuttingDown, (await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation)).Status);
		}

		[Fact]
		public async Task ShutdownDefersCleanupUntilInFlightCaptureIsClassified()
		{
			var id = Guid.NewGuid();
			var capture = new BlockingCapture(Batch(id, "A", "wall"));
			var provider = Provider(capture);
			Task<VirtualDesktopReconciliationResult> request = null;
			Exception requestError = null;
			var thread = new Thread(() =>
			{
				try { request = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); }
				catch (Exception ex) { requestError = ex; }
			});
			thread.Start();
			Assert.True(capture.Entered.Wait(TimeSpan.FromSeconds(10)), "The capture did not start.");

			provider.Dispose();

			Assert.NotNull(typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider));
			capture.Release.Set();
			Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The capture thread did not finish.");
			Assert.Null(requestError);
			Assert.Equal(VirtualDesktopReconciliationStatus.ShuttingDown, (await request).Status);
			Assert.Null(typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider));
		}

		[Fact]
		public async Task NewIngressSupersedesRetryTimerAndResetAndShutdownCancelTimers()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var delays = new ManualDelayScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(StructuralFailure(), Batch(id, "A", "wall"), StructuralFailure()), scheduler, delays);
			Desktop(provider, runtime, id, "A", "wall");
			var request = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll();
			Assert.Equal(1, delays.ActiveCount);

			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Moved, id, oldIndex: 0, newIndex: 0));
			scheduler.RunAll();

			Assert.Equal(0, delays.ActiveCount);
			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, (await request).Status);
			var failing = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation); scheduler.RunAll();
			Assert.Equal(1, delays.ActiveCount);
			provider.ResetRuntime();
			Assert.Equal(0, delays.ActiveCount);
			provider.Dispose();
			Assert.Equal(0, delays.ActiveCount);
			Assert.Equal(VirtualDesktopReconciliationStatus.SupersededByReset, (await failing).Status);
		}

		[Fact]
		public async Task OldEpochDtoAndWrapperCannotMutateNewRuntime()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall"), Batch(id, "A", "wall")), scheduler, new ManualDelayScheduler());
			var old = Desktop(provider, runtime, id, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var oldEpoch = provider.CurrentProviderEpoch;
			provider.ResetRuntime();
			var oldPublications = 0;
			EventHandler oldHandler = (_, __) => oldPublications++;
			VirtualDesktop.ApplicationViewChanged += oldHandler;
			provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged, providerEpoch: oldEpoch));
			scheduler.RunAll();

			try
			{
				Assert.Equal(0, oldPublications);
				Assert.Throws<InvalidOperationException>(() => old.Name = "write");
				Assert.Equal(0, runtime.SetNameCalls);
				var current = Desktop(provider, runtime, id, "A", "wall");
				current.Name = "current";
				Assert.Equal(1, runtime.SetNameCalls);
				Assert.Equal(2, provider.CurrentProviderEpoch);
			}
			finally { VirtualDesktop.ApplicationViewChanged -= oldHandler; }
		}

		[Fact]
		public async Task ContinuationCanReenterRequestWithoutProviderLockDeadlock()
		{
			var id = Guid.NewGuid();
			var capture = new FakeCapture(Batch(id, "A", "wall"), Batch(id, "A", "wall"));
			var provider = Provider(capture);
			var first = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var continuation = first.ContinueWith(_ => provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation)).Unwrap();

			var result = await continuation;

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, result.Status);
			Assert.Equal(2, capture.Calls);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task MatchingAcknowledgementDuringCaptureCannotValidateStaleRawValue(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			VirtualDesktopProvider provider = null;
			var capture = new CallbackCapture(
				() => PropertyBatch(id, property, "A"),
				() => { provider.EventPipeline.Accept(PropertyCallback(property, id, "C")); return PropertyBatch(id, property, "B"); },
				() => PropertyBatch(id, property, "C"));
			provider = Provider(runtime, capture, scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var published = new List<string>();
			provider.StableBatchPublished += (_, batch) => published.Add(PropertyValue(batch.Desktops[0], property));

			SetProperty(desktop, property, "C");
			scheduler.RunAll();

			Assert.Equal(new[] { "C" }, published);
			Assert.Equal("C", PropertyValue(desktop, property));
			Assert.Equal(0, provider.PendingWriteCount);
			Assert.Equal(3, capture.Calls);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task RawMismatchSupersedesPendingWithoutPublishingUnconfirmedTarget(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(PropertyBatch(id, property, "A"), PropertyBatch(id, property, "B")), scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");
			var faults = new List<VirtualDesktopProviderFault>();
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			SetProperty(desktop, property, "C");
			var confirmation = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);
			scheduler.RunAll();

			var result = await confirmation;
			Assert.Equal("B", PropertyValue(desktop, property));
			Assert.Equal("B", PropertyValue(result.Batch.Desktops[0], property));
			Assert.Equal(0, provider.PendingWriteCount);
			Assert.Contains(faults, fault => fault.FailureCategory == VirtualDesktopProviderFailureCategory.PropertySnapshot && fault.DesktopId == id);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task MissingNotificationConvergesPendingFromRawSnapshot(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(PropertyBatch(id, property, "A"), PropertyBatch(id, property, "C")), scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			SetProperty(desktop, property, "C");
			scheduler.RunAll();

			Assert.Equal("C", PropertyValue(provider.LastStableBatch.Desktops[0], property));
			Assert.Equal(0, provider.PendingWriteCount);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task OldAcknowledgementCannotClearNewerPendingForEitherProperty(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(PropertyBatch(id, property, "A"), PropertyBatch(id, property, "C")), scheduler, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;

			SetProperty(desktop, property, "B");
			SetProperty(desktop, property, "C");
			provider.EventPipeline.Accept(PropertyCallback(property, id, "B"));
			scheduler.RunAll();

			Assert.Equal("C", PropertyValue(desktop, property));
			Assert.Equal(0, provider.PendingWriteCount);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public async Task EmptyConfirmationSurvivesFailedAndNotAttemptedForEitherProperty(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(
				PropertyBatch(id, property, "A"),
				PropertyBatch(id, property, ""),
				PropertyBatch(id, property, Failed()),
				PropertyBatch(id, property, NotAttempted()),
				PropertyBatch(id, property, "")), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			delays.RunNext();
			delays.RunNext();
			delays.RunNext();

			Assert.Equal(string.Empty, PropertyValue(provider.LastStableBatch.Desktops[0], property));
			Assert.Equal(0, provider.EmptyCandidateCount);
		}

		[Theory]
		[InlineData((int)VirtualDesktopPropertyKind.Name)]
		[InlineData((int)VirtualDesktopPropertyKind.WallpaperPath)]
		public void ComFailureCreatesNoPendingForEitherProperty(int propertyValue)
		{
			var property = (VirtualDesktopPropertyKind)propertyValue;
			var id = Guid.NewGuid();
			var runtime = new FakeRuntime();
			if (property == VirtualDesktopPropertyKind.Name) runtime.SetNameError = new InvalidOperationException("synthetic");
			else runtime.SetWallpaperError = new InvalidOperationException("synthetic");
			var provider = Provider(runtime, new FakeCapture(PropertyBatch(id, property, "A")), null, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "A");

			Assert.Throws<InvalidOperationException>(() => SetProperty(desktop, property, "B"));

			Assert.Equal("A", PropertyValue(desktop, property));
			Assert.Equal(0, provider.PendingWriteCount);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task RemovedLkgCurrentIsClearedWhenCurrentReadIsNotUsable(bool unknownSuccess)
		{
			var removed = Guid.NewGuid();
			var remaining = Guid.NewGuid();
			var current = unknownSuccess ? VirtualDesktopReadResult<Guid>.Success(Guid.NewGuid()) : FailedGuid();
			var delays = new ManualDelayScheduler();
			var provider = Provider(new FakeCapture(
				Batch(removed, "A", "wall-A"),
				Batch(new[] { Entry(remaining, 0, "B", "wall-B") }, current)), null, delays);
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Null(result.Batch.CurrentDesktopId);
			Assert.Equal(VirtualDesktopReadStatus.Failed, result.Batch.CurrentDesktopReadStatus);
			Assert.Single(result.Batch.Desktops);
			Assert.Single(delays.Delays);
		}

		[Fact]
		public async Task AcceptedOldEpochIngressIsDiscardedAfterResetBeforeDrain()
		{
			var id = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall"), Batch(id, "A", "wall")), scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, id, "A", "wall");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var staticEvents = 0;
			var currentTransitions = 0;
			EventHandler view = (_, __) => staticEvents++;
			VirtualDesktop.ApplicationViewChanged += view;
			provider.CurrentTransitioned += (_, __) => currentTransitions++;
			try
			{
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged));
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, id, id));
				provider.ResetRuntime(false);
				scheduler.RunAll();

				Assert.Equal(0, staticEvents);
				Assert.Equal(0, currentTransitions);
			}
			finally { VirtualDesktop.ApplicationViewChanged -= view; }
		}

		[Fact]
		public async Task LegacyStablePublicationDefersReentrantCaptureUntilAllSubscribersObservePriorRevision()
		{
			var id = Guid.NewGuid();
			var capture = new FakeCapture(Batch(id, "A", "wall"), Batch(id, "B", "wall"));
			var provider = Provider(capture);
			Task<VirtualDesktopReconciliationResult> reentrant = null;
			var observed = new List<long>();
			provider.StableBatchPublished += (_, batch) => { if (batch.SnapshotRevision == 1) reentrant = provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation); };
			provider.StableBatchPublished += (_, batch) => observed.Add(batch.SnapshotRevision);

			var first = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var second = await reentrant;

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, first.Status);
			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, second.Status);
			Assert.Equal(new long[] { 1, 2 }, observed);
			Assert.Equal(2, capture.Calls);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task ReconciliationPropertyAndStablePublicationHoldCleanupLease(bool disposeFromPropertyChanging)
		{
			var id = Guid.NewGuid();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(id, "A", "wall"), Batch(id, "B", "wall")), null, new ManualDelayScheduler());
			var desktop = Desktop(provider, runtime, id, "A", "wall");
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var cleanupWasDeferred = false;
			Action dispose = () =>
			{
				provider.Dispose();
				cleanupWasDeferred = typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider) != null;
			};
			if (disposeFromPropertyChanging) desktop.PropertyChanging += (_, __) => dispose();
			else provider.StableBatchPublished += (_, __) => dispose();

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, result.Status);
			Assert.True(cleanupWasDeferred);
			Assert.Equal("B", desktop.Name);
			Assert.Null(typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider));
		}

		[Fact]
		public async Task CurrentTransitionPublicationHoldsCleanupLease()
		{
			var first = Guid.NewGuid();
			var second = Guid.NewGuid();
			var scheduler = new ManualEventScheduler();
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, new FakeCapture(Batch(new[] { Entry(first, 0, "A", "wa"), Entry(second, 1, "B", "wb") }, first)), scheduler, new ManualDelayScheduler());
			Desktop(provider, runtime, first, "A", "wa");
			Desktop(provider, runtime, second, "B", "wb");
			var initial = provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization); scheduler.RunAll(); await initial;
			var cleanupWasDeferred = false;
			var staticEvents = 0;
			EventHandler<VirtualDesktopChangedEventArgs> current = (_, __) => staticEvents++;
			VirtualDesktop.CurrentChanged += current;
			provider.CurrentTransitioned += (_, __) =>
			{
				provider.Dispose();
				cleanupWasDeferred = typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider) != null;
			};

			try
			{
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, first, second));
				scheduler.RunAll();

				Assert.True(cleanupWasDeferred);
				Assert.Equal(1, staticEvents);
				Assert.Null(typeof(VirtualDesktopProvider).GetField("_shutdownCleanup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider));
			}
			finally { VirtualDesktop.CurrentChanged -= current; }
		}

		[Fact]
		public async Task DisposeFromDirtyLegacyCallbackDoesNotSynchronouslyReenterRawCapture()
		{
			var id = Guid.NewGuid();
			var capture = new FakeCapture(Batch(id, "A", "wall"), Batch(id, "B", "wall"));
			var runtime = new FakeRuntime();
			var provider = Provider(runtime, capture, null, new ManualDelayScheduler());
			Desktop(provider, runtime, id, "A", "wall");
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			EventHandler<VirtualDesktopMovedEventArgs> moved = (_, __) => provider.Dispose();
			VirtualDesktop.Moved += moved;
			try { provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Moved, id, oldIndex: 0, newIndex: 0)); }
			finally { VirtualDesktop.Moved -= moved; }

			Assert.Equal(1, capture.Calls);
		}

		[Fact]
		public async Task RetryCallbackBeforeRegistrationInstallDoesNotStrandRecovery()
		{
			var id = Guid.NewGuid();
			var delays = new InlineDelayScheduler();
			var capture = new FakeCapture(StructuralFailure(), Batch(id, "A", "wall"));
			var provider = Provider(capture, null, delays, new ReconciliationRetryPolicy(TimeSpan.Zero));

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, result.Status);
			Assert.Equal(2, capture.Calls);
			Assert.Equal(1, delays.DisposedRegistrations);
		}

		[Fact]
		public void TimerDelaySchedulerPublishesZeroDelayExactlyOnceAfterRegistrationExists()
		{
			var scheduler = new TimerReconciliationDelayScheduler();
			var completed = new ManualResetEventSlim();
			var calls = 0;
			using (scheduler.Schedule(TimeSpan.Zero, () => { Interlocked.Increment(ref calls); completed.Set(); }))
			{
				Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "The zero-delay retry callback did not complete.");
			}
			Assert.Equal(1, calls);
		}

		[Fact]
		public void CancelledWaitersReleaseCancellationRegistrations()
		{
			var cancellation = new CancellationTokenSource();
			var waiters = Enumerable.Range(0, 64).Select(index => new ReconciliationWaiter(1, index + 1, cancellation.Token)).ToArray();

			cancellation.Cancel();

			Assert.True(SpinWait.SpinUntil(() => waiters.All(waiter => waiter.CancellationDisposed), TimeSpan.FromSeconds(10)), "Cancellation registrations were not released.");
			Assert.All(waiters, waiter => Assert.Equal(VirtualDesktopReconciliationStatus.Cancelled, waiter.Completion.Task.Result.Status));
		}

		private static void SetProperty(VirtualDesktop desktop, VirtualDesktopPropertyKind property, string value)
		{
			if (property == VirtualDesktopPropertyKind.Name) desktop.Name = value;
			else desktop.WallpaperPath = value;
		}

		private static string PropertyValue(VirtualDesktop desktop, VirtualDesktopPropertyKind property)
			=> property == VirtualDesktopPropertyKind.Name ? desktop.Name : desktop.WallpaperPath;

		private static string PropertyValue(VirtualDesktopStableEntry desktop, VirtualDesktopPropertyKind property)
			=> property == VirtualDesktopPropertyKind.Name ? desktop.Name : desktop.WallpaperPath;

		private static VirtualDesktopSnapshotBatch PropertyBatch(Guid id, VirtualDesktopPropertyKind property, string value)
			=> PropertyBatch(id, property, Success(value));

		private static VirtualDesktopSnapshotBatch PropertyBatch(Guid id, VirtualDesktopPropertyKind property, VirtualDesktopReadResult<string> value)
			=> property == VirtualDesktopPropertyKind.Name ? Batch(id, value, Success("other")) : Batch(id, Success("other"), value);

		private static VirtualDesktopCallbackDto PropertyCallback(VirtualDesktopPropertyKind property, Guid id, string value)
			=> new VirtualDesktopCallbackDto(property == VirtualDesktopPropertyKind.Name ? VirtualDesktopCallbackKind.Renamed : VirtualDesktopCallbackKind.WallpaperChanged, id, value: value);

		private static VirtualDesktopProvider Provider(IVirtualDesktopSnapshotCapture capture, IEventScheduler scheduler = null, IReconciliationDelayScheduler delays = null, ReconciliationRetryPolicy policy = null)
			=> Provider(new FakeRuntime(), capture, scheduler, delays ?? new ManualDelayScheduler(), policy);

		private static VirtualDesktopProvider Provider(FakeRuntime runtime, IVirtualDesktopSnapshotCapture capture, IEventScheduler scheduler, IReconciliationDelayScheduler delays, ReconciliationRetryPolicy policy = null)
		{
			var provider = new VirtualDesktopProvider(runtime, capture, delays, policy ?? new ReconciliationRetryPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(4)));
			if (scheduler != null) provider.EnableEventScheduling(scheduler, scheduler);
			return provider;
		}

		private static VirtualDesktop Desktop(VirtualDesktopProvider provider, FakeRuntime runtime, Guid id, string name, string wallpaper)
		{
#if NET5_0_OR_GREATER
			var desktop = (VirtualDesktop)RuntimeHelpers.GetUninitializedObject(typeof(VirtualDesktop));
#else
			var desktop = (VirtualDesktop)FormatterServices.GetUninitializedObject(typeof(VirtualDesktop));
#endif
			typeof(VirtualDesktop).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(desktop, provider);
			typeof(VirtualDesktop).GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(desktop, id);
			desktop.CommitNameMirror(name);
			desktop.CommitWallpaperMirror(wallpaper);
			provider.RegisterDesktop(desktop);
			return desktop;
		}

		private static VirtualDesktopSnapshotBatch Batch(Guid id, string name, string wallpaper)
			=> Batch(new[] { Entry(id, 0, name, wallpaper) }, id);
		private static VirtualDesktopSnapshotBatch Batch(Guid id, VirtualDesktopReadResult<string> name, VirtualDesktopReadResult<string> wallpaper)
			=> Batch(new[] { new VirtualDesktopSnapshotEntry(id, 0, name, wallpaper) }, VirtualDesktopReadResult<Guid>.Success(id));
		private static VirtualDesktopSnapshotBatch Batch(IEnumerable<VirtualDesktopSnapshotEntry> entries, Guid current)
			=> Batch(entries, VirtualDesktopReadResult<Guid>.Success(current));
		private static VirtualDesktopSnapshotBatch Batch(IEnumerable<VirtualDesktopSnapshotEntry> entries, VirtualDesktopReadResult<Guid> current)
		{
			var list = entries.ToList();
			return new VirtualDesktopSnapshotBatch(list, Array.Empty<VirtualDesktopSnapshotStructuralFailure>(), VirtualDesktopReadResult<int>.Success(list.Count), current, Capabilities(), Guid.NewGuid());
		}
		private static VirtualDesktopSnapshotEntry Entry(Guid id, int index, string name, string wallpaper)
			=> new VirtualDesktopSnapshotEntry(id, index, Success(name), Success(wallpaper));
		private static VirtualDesktopSnapshotBatch StructuralFailure()
			=> new VirtualDesktopSnapshotBatch(Array.Empty<VirtualDesktopSnapshotEntry>(), new[] { new VirtualDesktopSnapshotStructuralFailure(VirtualDesktopSnapshotStructuralFailureKind.CountReadFailed, null, VirtualDesktopReadResult<Guid>.NotAttempted(), NotAttempted(), NotAttempted()) }, VirtualDesktopReadResult<int>.Failed(VirtualDesktopReadErrorCategory.Count, null), FailedGuid(), Capabilities(), Guid.NewGuid());
		private static VirtualDesktopSnapshotCapabilities Capabilities() => new VirtualDesktopSnapshotCapabilities("synthetic", true, true, true, true);
		private static VirtualDesktopReadResult<string> Success(string value) => VirtualDesktopReadResult<string>.Success(value);
		private static VirtualDesktopReadResult<string> Failed() => VirtualDesktopReadResult<string>.Failed(VirtualDesktopReadErrorCategory.Property, unchecked((int)0x80004005));
		private static VirtualDesktopReadResult<string> NotAttempted() => VirtualDesktopReadResult<string>.NotAttempted();
		private static VirtualDesktopReadResult<string> Unsupported() => VirtualDesktopReadResult<string>.Unsupported();
		private static VirtualDesktopReadResult<string> Read(VirtualDesktopReadStatus status) => status == VirtualDesktopReadStatus.Failed ? Failed() : NotAttempted();
		private static VirtualDesktopReadResult<Guid> FailedGuid() => VirtualDesktopReadResult<Guid>.Failed(VirtualDesktopReadErrorCategory.CurrentDesktop, unchecked((int)0x80004005));

		private sealed class FakeCapture : IVirtualDesktopSnapshotCapture
		{
			private readonly Queue<VirtualDesktopSnapshotBatch> _batches;
			private VirtualDesktopSnapshotBatch _last;
			internal FakeCapture(params VirtualDesktopSnapshotBatch[] batches) => this._batches = new Queue<VirtualDesktopSnapshotBatch>(batches);
			internal int Calls { get; private set; }
			public VirtualDesktopSnapshotBatch Capture() { this.Calls++; if (this._batches.Count != 0) this._last = this._batches.Dequeue(); return this._last; }
		}

		private sealed class CallbackCapture : IVirtualDesktopSnapshotCapture
		{
			private readonly Queue<Func<VirtualDesktopSnapshotBatch>> _captures;
			internal CallbackCapture(params Func<VirtualDesktopSnapshotBatch>[] captures) => this._captures = new Queue<Func<VirtualDesktopSnapshotBatch>>(captures);
			internal int Calls { get; private set; }
			public VirtualDesktopSnapshotBatch Capture() { this.Calls++; return this._captures.Dequeue()(); }
		}

		private sealed class BlockingCapture : IVirtualDesktopSnapshotCapture
		{
			private readonly VirtualDesktopSnapshotBatch _batch;
			internal BlockingCapture(VirtualDesktopSnapshotBatch batch) => this._batch = batch;
			internal ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();
			internal ManualResetEventSlim Release { get; } = new ManualResetEventSlim();
			public VirtualDesktopSnapshotBatch Capture()
			{
				this.Entered.Set();
				if (!this.Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The synthetic capture release was not signaled.");
				return this._batch;
			}
		}

		private sealed class ManualDelayScheduler : IReconciliationDelayScheduler
		{
			private readonly Queue<ScheduledDelay> _pending = new Queue<ScheduledDelay>();
			internal List<TimeSpan> Delays { get; } = new List<TimeSpan>();
			internal int ActiveCount => this._pending.Count(x => x.IsActive);
			public IDisposable Schedule(TimeSpan delay, Action callback)
			{
				this.Delays.Add(delay);
				var scheduled = new ScheduledDelay(callback);
				this._pending.Enqueue(scheduled);
				return scheduled;
			}
			internal void RunNext()
			{
				while (this._pending.Count != 0)
				{
					var next = this._pending.Dequeue();
					if (next.Run()) return;
				}
				throw new InvalidOperationException("No retry was scheduled.");
			}
			private sealed class ScheduledDelay : IDisposable
			{
				private Action _callback;
				internal ScheduledDelay(Action callback) => this._callback = callback;
				internal bool IsActive => this._callback != null;
				internal bool Run() { var callback = Interlocked.Exchange(ref this._callback, null); if (callback == null) return false; callback(); return true; }
				public void Dispose() => Interlocked.Exchange(ref this._callback, null);
			}
		}

		private sealed class InlineDelayScheduler : IReconciliationDelayScheduler
		{
			internal int DisposedRegistrations { get; private set; }
			public IDisposable Schedule(TimeSpan delay, Action callback)
			{
				var registration = new DelegateDisposable(() => this.DisposedRegistrations++);
				callback();
				return registration;
			}

			private sealed class DelegateDisposable : IDisposable
			{
				private Action _dispose;
				internal DelegateDisposable(Action dispose) => this._dispose = dispose;
				public void Dispose() => Interlocked.Exchange(ref this._dispose, null)?.Invoke();
			}
		}

		private sealed class ManualEventScheduler : IEventScheduler
		{
			private readonly Queue<ScheduledEvent> _pending = new Queue<ScheduledEvent>();
			private readonly int _ownerThread = Environment.CurrentManagedThreadId;
			internal int PendingCount => this._pending.Count;
			public bool CheckAccess() => Environment.CurrentManagedThreadId == this._ownerThread;
			public EventScheduledOperation Post(Action drain)
			{
				var operation = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
				var scheduled = new ScheduledEvent(drain, operation);
				operation.SetAbort(scheduled.Abort);
				this._pending.Enqueue(scheduled);
				return operation;
			}
			internal void RunAll()
			{
				var guard = 0;
				while (this._pending.Count != 0)
				{
					if (++guard > 100) throw new InvalidOperationException("The scheduler did not quiesce.");
					this._pending.Dequeue().Run();
				}
			}
			private sealed class ScheduledEvent
			{
				private Action _action;
				private readonly EventScheduledOperation _operation;
				internal ScheduledEvent(Action action, EventScheduledOperation operation) { this._action = action; this._operation = operation; }
				internal bool Abort() { if (this._action == null) return false; this._action = null; this._operation.MarkAborted(); return true; }
				internal void Run() { var action = this._action; this._action = null; if (action == null) return; this._operation.MarkStarted(); action(); this._operation.MarkCompleted(); }
			}
		}

		[Fact]
		public async Task ReconciliationSubscriberFailuresDoNotStopMirrorPhasesOrFollowingEntries()
		{
			var firstId = Guid.NewGuid();
			var secondId = Guid.NewGuid();
			var runtime = new FakeRuntime();
			var capture = new FakeCapture(
				Batch(new[] { Entry(firstId, 0, "A", "wa"), Entry(secondId, 1, "X", "wx") }, firstId),
				Batch(new[] { Entry(firstId, 0, "B", "wb"), Entry(secondId, 1, "Y", "wy") }, secondId));
			var provider = Provider(runtime, capture, null, new ManualDelayScheduler());
			var first = Desktop(provider, runtime, firstId, "A", "wa");
			var second = Desktop(provider, runtime, secondId, "X", "wx");
			await provider.RequestReconciliationAsync(VirtualDesktopStableReason.Initialization);
			var stableFollowers = 0;
			var faults = new List<VirtualDesktopProviderFault>();
			first.PropertyChanged += (_, __) => throw new InvalidOperationException("property subscriber");
			provider.StableBatchPublished += (_, __) => throw new InvalidOperationException("stable subscriber");
			provider.StableBatchPublished += (_, __) => stableFollowers++;
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);

			var result = await provider.RequestReconciliationAsync(VirtualDesktopStableReason.ExplicitReconciliation);

			Assert.Equal(VirtualDesktopReconciliationStatus.Succeeded, result.Status);
			Assert.Equal("B", first.Name);
			Assert.Equal("Y", second.Name);
			Assert.Equal("wb", first.WallpaperPath);
			Assert.Equal("wy", second.WallpaperPath);
			Assert.Equal(1, stableFollowers);
			Assert.True(faults.Count(x => x.FailureCategory == VirtualDesktopProviderFailureCategory.Subscriber) >= 2);
		}

		private sealed class InlineEventScheduler : IEventScheduler
		{
			public bool CheckAccess() => true;
			public EventScheduledOperation Post(Action drain)
			{
				var operation = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
				operation.MarkStarted();
				drain();
				operation.MarkCompleted();
				return operation;
			}
		}

		private sealed class FakeRuntime : IVirtualDesktopProviderRuntime, IVirtualDesktopProviderRuntimeReset
		{
			private readonly Dictionary<Guid, VirtualDesktop> _desktops = new Dictionary<Guid, VirtualDesktop>();
			internal int SetNameCalls { get; private set; }
			internal int SetWallpaperCalls { get; private set; }
			internal Exception SetNameError { get; set; }
			internal Exception SetWallpaperError { get; set; }
			public bool TryResolveDesktop(Guid id, bool managedOnly, out VirtualDesktop desktop) => this._desktops.TryGetValue(id, out desktop);
			public void RegisterDesktop(VirtualDesktop desktop) => this._desktops[desktop.Id] = desktop;
			public void RemoveDesktop(Guid id) => this._desktops.Remove(id);
			public void SetDesktopName(VirtualDesktop desktop, string value) { this.SetNameCalls++; if (this.SetNameError != null) throw this.SetNameError; }
			public void SetDesktopWallpaper(VirtualDesktop desktop, string value) { this.SetWallpaperCalls++; if (this.SetWallpaperError != null) throw this.SetWallpaperError; }
			public void ResetManagedDesktops() => this._desktops.Clear();
		}
	}
}
