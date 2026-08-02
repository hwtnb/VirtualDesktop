using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WindowsDesktop.Internal;
using Xunit;

namespace WindowsDesktop.WPF.Tests
{
	public class DispatcherEventSchedulerTests
	{
		[Fact]
		public void ProcessMatchesRequestedArchitecture()
		{
#if EXPECTED_X86
			Assert.False(Environment.Is64BitProcess);
#elif EXPECTED_X64
			Assert.True(Environment.Is64BitProcess);
#else
			throw new InvalidOperationException("Expected architecture constant is missing.");
#endif
		}

		[Fact]
		public void DispatcherPostIsNonInlineAndRunsOnOwnerThread()
		{
			using (var host = new DispatcherHost())
			{
				var scheduler = new DispatcherEventScheduler(host.Dispatcher);
				var completed = new ManualResetEventSlim();
				var postingThread = Environment.CurrentManagedThreadId;
				var publicationThread = 0;
				var owner = false;
				var operation = scheduler.Post(() => { publicationThread = Environment.CurrentManagedThreadId; owner = host.Dispatcher.CheckAccess(); completed.Set(); });

				Assert.Equal(EventScheduleInitialStatus.Accepted, operation.InitialStatus);
				Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Dispatcher operation did not run.");
				SpinWait.SpinUntil(() => operation.Lifecycle == EventScheduleLifecycle.Completed, TimeSpan.FromSeconds(10));
				Assert.True(owner);
				Assert.NotEqual(postingThread, publicationThread);
				Assert.Equal(EventScheduleLifecycle.Completed, operation.Lifecycle);
			}
		}

		[Fact]
		public void CompletionBeforeHandlerRegistrationIsStillObserved()
		{
			using (var host = new DispatcherHost())
			{
				var drainFinished = new ManualResetEventSlim();
				DispatcherHookEventHandler operationPosted = null;
				operationPosted = (_, args) =>
				{
					host.Dispatcher.Hooks.OperationPosted -= operationPosted;
					Assert.False(host.Dispatcher.CheckAccess());
					args.Operation.Wait();
				};
				host.Dispatcher.Hooks.OperationPosted += operationPosted;
				var scheduler = new DispatcherEventScheduler(host.Dispatcher);
				var operation = scheduler.Post(drainFinished.Set);

				Assert.True(drainFinished.Wait(TimeSpan.FromSeconds(10)), "Dispatcher drain did not finish.");
				Assert.Equal(EventScheduleLifecycle.Completed, operation.Lifecycle);
			}
		}

		[Fact]
		public void AbortBeforeHandlerRegistrationIsStillObserved()
		{
			using (var host = new DispatcherHost())
			{
				var blockerStarted = new ManualResetEventSlim();
				var releaseBlocker = new ManualResetEventSlim();
				host.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
				{
					blockerStarted.Set();
					releaseBlocker.Wait(TimeSpan.FromSeconds(10));
				}));
				Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(10)), "Dispatcher blocker did not start.");
				DispatcherHookEventHandler operationPosted = null;
				operationPosted = (_, args) =>
				{
					host.Dispatcher.Hooks.OperationPosted -= operationPosted;
					Assert.True(args.Operation.Abort());
				};
				host.Dispatcher.Hooks.OperationPosted += operationPosted;
				var ran = false;
				var operation = new DispatcherEventScheduler(host.Dispatcher).Post(() => ran = true);
				releaseBlocker.Set();
				Assert.False(ran);
				Assert.Equal(EventScheduleInitialStatus.Accepted, operation.InitialStatus);
				Assert.Equal(EventScheduleLifecycle.Aborted, operation.Lifecycle);
			}
		}

		[Fact]
		public void AcceptedOperationCanBeAbortedBeforeDrain()
		{
			using (var host = new DispatcherHost())
			{
				var blockerStarted = new ManualResetEventSlim();
				var release = new ManualResetEventSlim();
				host.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { blockerStarted.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
				Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(10)), "Dispatcher blocker did not start.");
				var ran = false;
				var operation = new DispatcherEventScheduler(host.Dispatcher).Post(() => ran = true);
				Assert.True(operation.TryAbort());
				release.Set();
				SpinWait.SpinUntil(() => operation.Lifecycle == EventScheduleLifecycle.Aborted, TimeSpan.FromSeconds(10));

				Assert.False(ran);
				Assert.Equal(EventScheduleLifecycle.Aborted, operation.Lifecycle);
			}
		}

		[Fact]
		public void ShutdownDispatcherRejectsNewPost()
		{
			var host = new DispatcherHost();
			var dispatcher = host.Dispatcher;
			host.Dispose();
			var operation = new DispatcherEventScheduler(dispatcher).Post(() => { });
			Assert.Equal(EventScheduleInitialStatus.Rejected, operation.InitialStatus);
			Assert.Equal(EventScheduleLifecycle.Rejected, operation.Lifecycle);
		}

		[Fact]
		public void FacadeIsIdempotentForSameDispatcherAndRejectsRebindOrLateEnable()
		{
			using (var first = new DispatcherHost())
			using (var second = new DispatcherHost())
			{
				var provider = new VirtualDesktopProvider();
				Assert.Same(provider, provider.EnableDispatcherEventScheduling(first.Dispatcher));
				Assert.Same(provider, provider.EnableDispatcherEventScheduling(first.Dispatcher));
				Assert.Throws<InvalidOperationException>(() => provider.EnableDispatcherEventScheduling(second.Dispatcher));

				var initialized = new VirtualDesktopProvider();
				typeof(VirtualDesktopProvider).GetField("_initializationTask", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(initialized, Task.CompletedTask);
				Assert.Throws<InvalidOperationException>(() => initialized.EnableDispatcherEventScheduling(first.Dispatcher));
			}
		}

		[Fact]
		public void IngressPublicationRunsOnDispatcherAndUsesSinglePost()
		{
			using (var host = new DispatcherHost())
			{
				var completed = new ManualResetEventSlim();
				var count = 0;
				var ingress = new EventIngress<int>(new DispatcherEventScheduler(host.Dispatcher), item =>
				{
					Assert.True(host.Dispatcher.CheckAccess());
					if (Interlocked.Increment(ref count) == 10) completed.Set();
				}, (_, __) => { });
				for (var i = 0; i < 10; i++) ingress.Enqueue(i);

				Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Dispatcher ingress did not drain.");
				Assert.Equal(1, ingress.PostCount);
				Assert.Equal(0, ingress.PendingCount);
			}
		}

		private sealed class DispatcherHost : IDisposable
		{
			private readonly Thread _thread;
			private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
			internal DispatcherHost()
			{
				this._thread = new Thread(() =>
				{
					this.Dispatcher = Dispatcher.CurrentDispatcher;
					this._ready.Set();
					Dispatcher.Run();
				});
				this._thread.SetApartmentState(ApartmentState.STA);
				this._thread.IsBackground = true;
				this._thread.Start();
				if (!this._ready.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dispatcher did not start.");
			}

			internal Dispatcher Dispatcher { get; private set; }

			public void Dispose()
			{
				if (!this.Dispatcher.HasShutdownStarted) this.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
				if (!this._thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dispatcher did not shut down.");
				this._ready.Dispose();
			}
		}
	}
}
