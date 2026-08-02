using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using WindowsDesktop.Internal;

namespace WindowsDesktop
{
	partial class VirtualDesktop : INotifyPropertyChanging, INotifyPropertyChanged
	{
		/// <summary>
		/// Occurs when a virtual desktop property changing.
		/// </summary>
		public event PropertyChangingEventHandler PropertyChanging;

		internal void RaisePropertyChangingSafely(string propertyName, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, VirtualDesktopProviderEventKind eventKind, long sequence)
		{
			var handlers = this.PropertyChanging;
			if (handlers == null) return;
			var args = new PropertyChangingEventArgs(propertyName);
			foreach (PropertyChangingEventHandler handler in handlers.GetInvocationList())
				pipeline.InvokeSubscriber(handler, () => handler(this, args), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.PropertyChanging, this.Id, sequence, exceptions);
		}

		/// <summary>
		/// Occurs when a virtual desktop property changed.
		/// </summary>
		public event PropertyChangedEventHandler PropertyChanged;

		internal void RaisePropertyChangedSafely(string propertyName, VirtualDesktopEventPipeline pipeline, ExceptionCollector exceptions, VirtualDesktopProviderEventKind eventKind, long sequence)
		{
			var handlers = this.PropertyChanged;
			if (handlers == null) return;
			var args = new PropertyChangedEventArgs(propertyName);
			foreach (PropertyChangedEventHandler handler in handlers.GetInvocationList())
				pipeline.InvokeSubscriber(handler, () => handler(this, args), VirtualDesktopProviderFaultPhase.EventDispatch, VirtualDesktopProviderEventKind.PropertyChanged, this.Id, sequence, exceptions);
		}
	}
}
