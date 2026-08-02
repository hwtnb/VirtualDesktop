using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using WindowsDesktop.Interop;
using WindowsDesktop.Internal;
using Xunit;

namespace WindowsDesktop.Tests
{
	public class ProductionEventPipelineTests
	{
		[Fact]
		public void ExplicitSameValueSettersAlwaysCallRuntimeWithoutNotifications()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "same", "same-path");
			var propertyNotifications = 0;
			var nameEvents = 0;
			var wallpaperEvents = 0;
			desktop.PropertyChanging += (_, __) => propertyNotifications++;
			desktop.PropertyChanged += (_, __) => propertyNotifications++;
			EventHandler<VirtualDesktopRenamedEventArgs> renamed = (_, __) => nameEvents++;
			EventHandler<VirtualDesktopWallpaperChangedEventArgs> wallpaper = (_, __) => wallpaperEvents++;
			VirtualDesktop.Renamed += renamed;
			VirtualDesktop.WallpaperChanged += wallpaper;
			try
			{
				desktop.Name = "same";
				desktop.WallpaperPath = "same-path";
				Assert.Equal(1, runtime.SetNameCalls);
				Assert.Equal(1, runtime.SetWallpaperCalls);
				Assert.Equal(0, propertyNotifications);
				Assert.Equal(0, nameEvents);
				Assert.Equal(0, wallpaperEvents);
			}
			finally
			{
				VirtualDesktop.Renamed -= renamed;
				VirtualDesktop.WallpaperChanged -= wallpaper;
			}
		}

		[Fact]
		public void LegacySynchronousSetterCallbackIsTheSingleStaticEventSource()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var phases = new List<string>();
			desktop.PropertyChanging += (_, __) => phases.Add("changing:" + desktop.Name);
			desktop.PropertyChanged += (_, __) => phases.Add("changed:" + desktop.Name);
			EventHandler<VirtualDesktopRenamedEventArgs> renamed = (_, args) => phases.Add("static:" + args.OldName + "->" + args.NewName);
			VirtualDesktop.Renamed += renamed;
			try
			{
				runtime.OnSetName = (target, value) => provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, target.Id, value: value));
				desktop.Name = "B";
				phases.Add("return:" + desktop.Name);
				Assert.Equal(new[] { "changing:A", "changed:B", "static:A->B", "return:B" }, phases);
				Assert.Equal(1, runtime.SetNameCalls);
			}
			finally { VirtualDesktop.Renamed -= renamed; }
		}

		[Fact]
		public void LegacyDelayedAndExternalPropertyCallbacksUpdateMirrorAndPublish()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var renamedEvents = new List<string>();
			var wallpaperEvents = new List<string>();
			var legacySender = new object();
			EventHandler<VirtualDesktopRenamedEventArgs> renamed = (sender, args) => { Assert.Same(legacySender, sender); renamedEvents.Add(args.OldName + "->" + args.NewName); };
			EventHandler<VirtualDesktopWallpaperChangedEventArgs> wallpaper = (sender, args) => { Assert.Same(legacySender, sender); wallpaperEvents.Add(args.OldPath + "->" + args.NewPath); };
			VirtualDesktop.Renamed += renamed;
			VirtualDesktop.WallpaperChanged += wallpaper;
			try
			{
				desktop.Name = "B";
				Assert.Empty(renamedEvents);
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, desktop.Id, value: "B"), legacySender);
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, desktop.Id, value: "C"), legacySender);
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.WallpaperChanged, desktop.Id, value: "wall-B"), legacySender);
				Assert.Equal(new[] { "B->B", "B->C" }, renamedEvents);
				Assert.Equal(new[] { "wall-A->wall-B" }, wallpaperEvents);
				Assert.Equal("C", desktop.Name);
				Assert.Equal("wall-B", desktop.WallpaperPath);
			}
			finally
			{
				VirtualDesktop.Renamed -= renamed;
				VirtualDesktop.WallpaperChanged -= wallpaper;
			}
		}

		[Fact]
		public void StrongSetterUsesProductionPathAndRejectsOffOwnerAndReentryBeforeCom()
		{
			var runtime = new FakeRuntime();
			var scheduler = new ManualScheduler();
			var provider = StrongProvider(runtime, scheduler);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var faults = new List<VirtualDesktopProviderFault>();
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);
			var reentrantCalls = 0;
			desktop.PropertyChanging += (_, __) =>
			{
				Assert.Throws<InvalidOperationException>(() => desktop.Name = "reentrant");
				reentrantCalls++;
			};
			desktop.Name = "B";
			Assert.Equal("B", desktop.Name);
			Assert.Equal(1, runtime.SetNameCalls);
			Assert.Equal(1, reentrantCalls);

			Exception error = null;
			var thread = new Thread(() =>
			{
				try { desktop.Name = "off-owner"; }
				catch (Exception ex) { error = ex; }
			});
			thread.Start();
			Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Off-owner setter test hung.");
			Assert.IsType<InvalidOperationException>(error);
			Assert.Equal(1, runtime.SetNameCalls);
			Assert.Empty(faults);
		}

		[Fact]
		public void RuntimeFailureLeavesMirrorAndEventsUnchanged()
		{
			var runtime = new FakeRuntime { SetNameError = new InvalidOperationException("synthetic") };
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var notifications = 0;
			desktop.PropertyChanging += (_, __) => notifications++;
			desktop.PropertyChanged += (_, __) => notifications++;
			Assert.Throws<InvalidOperationException>(() => desktop.Name = "B");
			Assert.Equal("A", desktop.Name);
			Assert.Equal(1, runtime.SetNameCalls);
			Assert.Equal(0, notifications);
		}

		[Fact]
		public void DestroyedUsesManagedTombstoneAndUnresolvedDtoDoesNotStopFollowingDto()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var destroyed = CreateDesktop(provider, runtime, "A", "wall-A");
			var fallback = CreateDesktop(provider, runtime, "B", "wall-B");
			var destroyedCount = 0;
			var viewCount = 0;
			var faults = new List<VirtualDesktopProviderFault>();
			EventHandler<VirtualDesktopDestroyEventArgs> destroyedHandler = (_, args) => { Assert.Same(destroyed, args.Destroyed); destroyedCount++; };
			EventHandler viewHandler = (_, __) => viewCount++;
			VirtualDesktop.Destroyed += destroyedHandler;
			VirtualDesktop.ApplicationViewChanged += viewHandler;
			provider.EventDispatchFaulted += (_, fault) => faults.Add(fault);
			try
			{
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Destroyed, destroyed.Id, fallback.Id));
				Assert.Equal(1, destroyedCount);
				Assert.Equal(0, runtime.LiveResolveCalls);
				Assert.False(runtime.Contains(destroyed.Id));
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Destroyed, Guid.NewGuid(), fallback.Id));
				provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged));
				Assert.Single(faults);
				Assert.Equal(VirtualDesktopProviderEventKind.Destroyed, faults[0].EventKind);
				Assert.Equal(1, viewCount);
			}
			finally
			{
				VirtualDesktop.Destroyed -= destroyedHandler;
				VirtualDesktop.ApplicationViewChanged -= viewHandler;
			}
		}

		[Fact]
		public void LegacyEventRaiserPublishesEveryTopologyCallbackKindThroughRuntime()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var first = CreateDesktop(provider, runtime, "A", "wall-A");
			var second = CreateDesktop(provider, runtime, "B", "wall-B");
			var legacySender = new object();
			var counts = new int[9];
			EventHandler<VirtualDesktop> created = (sender, __) => { Assert.Same(legacySender, sender); counts[0]++; };
			EventHandler<VirtualDesktopDestroyEventArgs> begin = (sender, __) => { Assert.Same(legacySender, sender); counts[1]++; };
			EventHandler<VirtualDesktopDestroyEventArgs> failed = (sender, __) => { Assert.Same(legacySender, sender); counts[2]++; };
			EventHandler<VirtualDesktopMovedEventArgs> moved = (sender, __) => { Assert.Same(legacySender, sender); counts[3]++; };
			EventHandler<VirtualDesktopChangedEventArgs> current = (sender, __) => { Assert.Same(legacySender, sender); counts[4]++; };
			EventHandler<VirtualDesktop> switched = (sender, __) => { Assert.Same(legacySender, sender); counts[5]++; };
			EventHandler<VirtualDesktop> remote = (sender, __) => { Assert.Same(legacySender, sender); counts[6]++; };
			EventHandler view = (sender, __) => { Assert.Same(legacySender, sender); counts[7]++; };
			EventHandler<VirtualDesktopDestroyEventArgs> destroyed = (sender, __) => { Assert.Same(legacySender, sender); counts[8]++; };
			VirtualDesktop.Created += created;
			VirtualDesktop.DestroyBegin += begin;
			VirtualDesktop.DestroyFailed += failed;
			VirtualDesktop.Moved += moved;
			VirtualDesktop.CurrentChanged += current;
			VirtualDesktop.DesktopSwitched += switched;
			VirtualDesktop.RemoteDesktopConnected += remote;
			VirtualDesktop.ApplicationViewChanged += view;
			VirtualDesktop.Destroyed += destroyed;
			try
			{
				var pipeline = provider.EventPipeline;
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Created, first.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DestroyBegin, first.Id, second.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DestroyFailed, first.Id, second.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Moved, first.Id, oldIndex: 1, newIndex: 0), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.CurrentChanged, first.Id, second.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DesktopSwitched, first.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.RemoteDesktopConnected, first.Id), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.ApplicationViewChanged), legacySender);
				pipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Destroyed, first.Id, second.Id), legacySender);
				Assert.All(counts, count => Assert.Equal(1, count));
			}
			finally
			{
				VirtualDesktop.Created -= created;
				VirtualDesktop.DestroyBegin -= begin;
				VirtualDesktop.DestroyFailed -= failed;
				VirtualDesktop.Moved -= moved;
				VirtualDesktop.CurrentChanged -= current;
				VirtualDesktop.DesktopSwitched -= switched;
				VirtualDesktop.RemoteDesktopConnected -= remote;
				VirtualDesktop.ApplicationViewChanged -= view;
				VirtualDesktop.Destroyed -= destroyed;
			}
		}

		[Fact]
		public void LegacyShutdownRejectsWithoutResolutionOrPublication()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			provider.EventPipeline.Shutdown();
			var result = provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DesktopSwitched, desktop.Id));
			Assert.Equal(EventEnqueueStatus.Rejected, result.Status);
			Assert.Equal(EventEnqueueRejectionReason.Shutdown, result.RejectionReason);
			Assert.Equal(0, runtime.ResolveCalls);
		}

		[Fact]
		public void LegacyAcceptedPublicationLeaseDefersShutdownCompletionUntilPublicationEnds()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var resolveEntered = new ManualResetEventSlim();
			var releaseResolve = new ManualResetEventSlim();
			var shutdownCompleted = new ManualResetEventSlim();
			var legacySender = new object();
			var events = 0;
			runtime.BeforeResolve = () =>
			{
				resolveEntered.Set();
				Assert.True(releaseResolve.Wait(TimeSpan.FromSeconds(10)), "Resolve barrier was not released.");
			};
			EventHandler<VirtualDesktop> handler = (sender, target) => { Assert.Same(legacySender, sender); Assert.Same(desktop, target); events++; };
			VirtualDesktop.DesktopSwitched += handler;
			EventEnqueueResult accepted = null;
			Exception callbackError = null;
			var callbackThread = new Thread(() =>
			{
				try { accepted = provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DesktopSwitched, desktop.Id), legacySender); }
				catch (Exception ex) { callbackError = ex; }
			});
			try
			{
				callbackThread.Start();
				Assert.True(resolveEntered.Wait(TimeSpan.FromSeconds(10)), "Accepted callback did not reach resolution.");
				provider.EventPipeline.Shutdown(shutdownCompleted.Set);
				Assert.False(shutdownCompleted.IsSet);
				Assert.Equal(0, events);
				releaseResolve.Set();
				Assert.True(callbackThread.Join(TimeSpan.FromSeconds(10)), "Accepted callback did not finish.");
				Assert.Null(callbackError);
				Assert.Equal(EventEnqueueStatus.Accepted, accepted.Status);
				Assert.True(shutdownCompleted.Wait(TimeSpan.FromSeconds(10)), "Shutdown completion did not follow publication.");
				Assert.Equal(1, events);

				var rejected = provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.DesktopSwitched, desktop.Id), legacySender);
				Assert.Equal(EventEnqueueStatus.Rejected, rejected.Status);
				Assert.Equal(1, runtime.ResolveCalls);
				Assert.Equal(1, events);
			}
			finally
			{
				releaseResolve.Set();
				if (callbackThread.IsAlive) callbackThread.Join(TimeSpan.FromSeconds(10));
				VirtualDesktop.DesktopSwitched -= handler;
			}
		}

		[Fact]
		public void NotificationCapturePreservesLegacyListenerAsStaticEventSender()
		{
			var provider = new VirtualDesktopProvider(new FakeRuntime());
			var notification = new TestNotification();
			typeof(VirtualDesktopNotification).GetField("_pipeline", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(notification, provider.EventPipeline);
			typeof(VirtualDesktopNotification).GetField("_materializer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(notification, new VirtualDesktopCallbackMaterializer(typeof(INotificationDesktop)));
			object observedSender = null;
			EventHandler handler = (sender, __) => observedSender = sender;
			VirtualDesktop.ApplicationViewChanged += handler;
			try
			{
				notification.RaiseApplicationViewChanged();
				Assert.Same(notification, observedSender);
			}
			finally { VirtualDesktop.ApplicationViewChanged -= handler; }
		}

		[Fact]
		public void FaultRecursionGuardIsProviderScoped()
		{
			var first = new VirtualDesktopProvider(new FakeRuntime());
			var secondRuntime = new FakeRuntime();
			var second = StrongProvider(secondRuntime, new ManualScheduler());
			var secondDesktop = CreateDesktop(second, secondRuntime, "A", "wall-A");
			var secondFaults = 0;
			second.EventDispatchFaulted += (_, __) => secondFaults++;
			first.EventDispatchFaulted += (_, __) =>
			{
				second.ReportEventFault(Fault());
				secondDesktop.Name = "B";
			};
			first.ReportEventFault(Fault());
			Assert.Equal(1, secondFaults);
			Assert.Equal(1, secondRuntime.SetNameCalls);
			Assert.Equal("B", secondDesktop.Name);
		}

		[Fact]
		public void LegacySubscriberFailuresCompleteAllRealTransitionPhasesThenRethrowFirst()
		{
			var runtime = new FakeRuntime();
			var provider = new VirtualDesktopProvider(runtime);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var first = new InvalidOperationException("first");
			var phases = new List<string>();
			PropertyChangingEventHandler changingFirst = (_, __) => throw first;
			PropertyChangingEventHandler changingSecond = (_, __) => phases.Add("changing:" + desktop.Name);
			PropertyChangedEventHandler changedFirst = (_, __) => throw new InvalidOperationException("changed");
			PropertyChangedEventHandler changedSecond = (_, __) => phases.Add("changed:" + desktop.Name);
			EventHandler<VirtualDesktopRenamedEventArgs> staticFirst = (_, __) => throw new InvalidOperationException("static");
			EventHandler<VirtualDesktopRenamedEventArgs> staticSecond = (sender, args) => { Assert.Same(provider, sender); phases.Add("static:" + args.OldName + "->" + args.NewName); };
			desktop.PropertyChanging += changingFirst;
			desktop.PropertyChanging += changingSecond;
			desktop.PropertyChanged += changedFirst;
			desktop.PropertyChanged += changedSecond;
			VirtualDesktop.Renamed += staticFirst;
			VirtualDesktop.Renamed += staticSecond;
			try
			{
				var error = Assert.Throws<InvalidOperationException>(() => provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, desktop.Id, value: "B")));
				Assert.Same(first, error);
				Assert.Equal("B", desktop.Name);
				Assert.Equal(new[] { "changing:A", "changed:B", "static:A->B" }, phases);
			}
			finally
			{
				desktop.PropertyChanging -= changingFirst;
				desktop.PropertyChanging -= changingSecond;
				desktop.PropertyChanged -= changedFirst;
				desktop.PropertyChanged -= changedSecond;
				VirtualDesktop.Renamed -= staticFirst;
				VirtualDesktop.Renamed -= staticSecond;
			}
		}

		[Fact]
		public void StrongRealSetterFaultsSubscribersWithoutStoppingCommitOrRemainingPhases()
		{
			var runtime = new FakeRuntime();
			var provider = StrongProvider(runtime, new ManualScheduler());
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var phases = new List<string>();
			var faults = new List<VirtualDesktopProviderFault>();
			provider.EventDispatchFaulted += (_, fault) => { faults.Add(fault); throw new InvalidOperationException("fault subscriber"); };
			desktop.PropertyChanging += (_, __) => throw new InvalidOperationException("changing");
			desktop.PropertyChanging += (_, __) => phases.Add("changing:" + desktop.Name);
			desktop.PropertyChanged += (_, __) => throw new InvalidOperationException("changed");
			desktop.PropertyChanged += (_, __) => phases.Add("changed:" + desktop.Name);
			EventHandler<VirtualDesktopRenamedEventArgs> staticFirst = (_, __) => throw new InvalidOperationException("static");
			EventHandler<VirtualDesktopRenamedEventArgs> staticSecond = (sender, args) => { Assert.Same(provider, sender); phases.Add("static:" + args.OldName + "->" + args.NewName); };
			VirtualDesktop.Renamed += staticFirst;
			VirtualDesktop.Renamed += staticSecond;
			try
			{
				desktop.Name = "B";
				Assert.Equal("B", desktop.Name);
				Assert.Equal(new[] { "changing:A", "changed:B", "static:A->B" }, phases);
				Assert.Equal(3, faults.Count);
				Assert.Equal(new[] { VirtualDesktopProviderEventKind.PropertyChanging, VirtualDesktopProviderEventKind.PropertyChanged, VirtualDesktopProviderEventKind.Renamed }, new[] { faults[0].EventKind, faults[1].EventKind, faults[2].EventKind });
			}
			finally
			{
				VirtualDesktop.Renamed -= staticFirst;
				VirtualDesktop.Renamed -= staticSecond;
			}
		}

		[Fact]
		public void StrongPropertyCallbackIsQueuedOnceAndDoesNotAdoptBeforeReconciliation()
		{
			var runtime = new FakeRuntime();
			var scheduler = new ManualScheduler();
			var provider = StrongProvider(runtime, scheduler);
			var desktop = CreateDesktop(provider, runtime, "A", "wall-A");
			var propertyEvents = 0;
			var staticEvents = 0;
			desktop.PropertyChanged += (_, __) => propertyEvents++;
			EventHandler<VirtualDesktopRenamedEventArgs> renamed = (_, __) => staticEvents++;
			VirtualDesktop.Renamed += renamed;
			try
			{
				var accepted = provider.EventPipeline.Accept(new VirtualDesktopCallbackDto(VirtualDesktopCallbackKind.Renamed, desktop.Id, value: "B"));
				Assert.Equal(EventEnqueueStatus.Accepted, accepted.Status);
				Assert.Equal("A", desktop.Name);
				Assert.Equal(1, provider.EventPipeline.PendingCount);
				scheduler.Run();
				Assert.Equal("A", desktop.Name);
				Assert.Equal(0, propertyEvents);
				Assert.Equal(0, staticEvents);
				Assert.Equal(0, provider.EventPipeline.PendingCount);
			}
			finally { VirtualDesktop.Renamed -= renamed; }
		}

		private static VirtualDesktopProviderFault Fault()
			=> new VirtualDesktopProviderFault(VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.Unknown, null, typeof(InvalidOperationException).FullName, null, 0);

		private static VirtualDesktopProvider StrongProvider(FakeRuntime runtime, IEventScheduler scheduler)
		{
			var provider = new VirtualDesktopProvider(runtime);
			provider.EnableEventScheduling(scheduler, scheduler);
			return provider;
		}

		private static VirtualDesktop CreateDesktop(VirtualDesktopProvider provider, FakeRuntime runtime, string name, string wallpaper)
		{
#if NET5_0_OR_GREATER
			var desktop = (VirtualDesktop)RuntimeHelpers.GetUninitializedObject(typeof(VirtualDesktop));
#else
			var desktop = (VirtualDesktop)FormatterServices.GetUninitializedObject(typeof(VirtualDesktop));
#endif
			typeof(VirtualDesktop).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(desktop, provider);
			typeof(VirtualDesktop).GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(desktop, Guid.NewGuid());
			desktop.CommitNameMirror(name);
			desktop.CommitWallpaperMirror(wallpaper);
			provider.RegisterDesktop(desktop);
			return desktop;
		}

		private sealed class FakeRuntime : IVirtualDesktopProviderRuntime
		{
			private readonly Dictionary<Guid, VirtualDesktop> _desktops = new Dictionary<Guid, VirtualDesktop>();
			internal int SetNameCalls { get; private set; }
			internal int SetWallpaperCalls { get; private set; }
			internal int ResolveCalls { get; private set; }
			internal int LiveResolveCalls { get; private set; }
			internal Exception SetNameError { get; set; }
			internal Action<VirtualDesktop, string> OnSetName { get; set; }
			internal Action BeforeResolve { get; set; }
			internal bool Contains(Guid id) => this._desktops.ContainsKey(id);
			public bool TryResolveDesktop(Guid id, bool managedOnly, out VirtualDesktop desktop)
			{
				this.ResolveCalls++;
				this.BeforeResolve?.Invoke();
				if (this._desktops.TryGetValue(id, out desktop)) return true;
				if (!managedOnly) this.LiveResolveCalls++;
				return false;
			}
			public void RegisterDesktop(VirtualDesktop desktop) => this._desktops[desktop.Id] = desktop;
			public void RemoveDesktop(Guid id) => this._desktops.Remove(id);
			public void SetDesktopName(VirtualDesktop desktop, string value)
			{
				this.SetNameCalls++;
				if (this.SetNameError != null) throw this.SetNameError;
				this.OnSetName?.Invoke(desktop, value);
			}
			public void SetDesktopWallpaper(VirtualDesktop desktop, string value) => this.SetWallpaperCalls++;
		}

		private interface INotificationDesktop
		{
			Guid GetID();
		}

		private sealed class TestNotification : VirtualDesktopNotification
		{
			internal void RaiseApplicationViewChanged() => this.ViewVirtualDesktopChangedCore(null);
		}

		private sealed class ManualScheduler : IEventScheduler
		{
			private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
			private Action _drain;
			private EventScheduledOperation _operation;
			public bool CheckAccess() => Environment.CurrentManagedThreadId == this._ownerThreadId;
			public EventScheduledOperation Post(Action drain)
			{
				this._drain = drain;
				this._operation = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
				this._operation.SetAbort(() => { this._drain = null; this._operation.MarkAborted(); return true; });
				return this._operation;
			}
			internal void Run()
			{
				var drain = this._drain;
				this._drain = null;
				this._operation.MarkStarted();
				drain();
				this._operation.MarkCompleted();
			}
		}
	}
}
