using System;
using System.Windows.Threading;
using WindowsDesktop.Internal;

namespace WindowsDesktop
{
	public static class VirtualDesktopProviderExtensions
	{
		public static VirtualDesktopProvider EnableDispatcherEventScheduling(this VirtualDesktopProvider provider, Dispatcher dispatcher)
		{
			if (provider == null) throw new ArgumentNullException(nameof(provider));
			if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
			provider.EnableEventScheduling(new DispatcherEventScheduler(dispatcher), dispatcher);
			return provider;
		}
	}

	internal sealed class DispatcherEventScheduler : IEventScheduler
	{
		private readonly Dispatcher _dispatcher;

		internal DispatcherEventScheduler(Dispatcher dispatcher)
		{
			this._dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		}

		public bool CheckAccess() => this._dispatcher.CheckAccess();

		public EventScheduledOperation Post(Action drain)
		{
			if (drain == null) throw new ArgumentNullException(nameof(drain));
			if (this._dispatcher.HasShutdownStarted || this._dispatcher.HasShutdownFinished) return new EventScheduledOperation(EventScheduleInitialStatus.Rejected);

			var result = new EventScheduledOperation(EventScheduleInitialStatus.Accepted);
			try
			{
				var dispatcherOperation = this._dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { result.MarkStarted(); drain(); }));
				dispatcherOperation.Completed += (_, __) => result.MarkCompleted();
				dispatcherOperation.Aborted += (_, __) => result.MarkAborted();
				result.SetAbort(dispatcherOperation.Abort);
				SynchronizeTerminalStatus(dispatcherOperation, result);
				return result;
			}
			catch (InvalidOperationException)
			{
				return new EventScheduledOperation(EventScheduleInitialStatus.Rejected);
			}
		}

		private static void SynchronizeTerminalStatus(DispatcherOperation operation, EventScheduledOperation result)
		{
			if (operation.Status == DispatcherOperationStatus.Completed) result.MarkCompleted();
			else if (operation.Status == DispatcherOperationStatus.Aborted) result.MarkAborted();
		}
	}
}
