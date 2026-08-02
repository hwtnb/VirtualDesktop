using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WindowsDesktop
{
	public enum VirtualDesktopReadStatus
	{
		Success,
		Unsupported,
		Failed,
		NotAttempted,
	}

	public enum VirtualDesktopStableReason
	{
		Initialization,
		TopologyChanged,
		PropertyChanged,
		LocalWrite,
		ExplicitReconciliation,
		Recovery,
	}

	public sealed class VirtualDesktopStableEntry
	{
		public VirtualDesktopStableEntry(Guid id, int orderIndex, string name, VirtualDesktopReadStatus nameReadStatus, string wallpaperPath, VirtualDesktopReadStatus wallpaperPathReadStatus)
		{
			if (nameReadStatus == VirtualDesktopReadStatus.Success && name == null) throw new ArgumentNullException(nameof(name));
			if (wallpaperPathReadStatus == VirtualDesktopReadStatus.Success && wallpaperPath == null) throw new ArgumentNullException(nameof(wallpaperPath));

			this.Id = id;
			this.OrderIndex = orderIndex;
			this.Name = name;
			this.NameReadStatus = nameReadStatus;
			this.WallpaperPath = wallpaperPath;
			this.WallpaperPathReadStatus = wallpaperPathReadStatus;
		}

		public Guid Id { get; }
		public int OrderIndex { get; }
		public string Name { get; }
		public VirtualDesktopReadStatus NameReadStatus { get; }
		public string WallpaperPath { get; }
		public VirtualDesktopReadStatus WallpaperPathReadStatus { get; }
	}

	public sealed class VirtualDesktopStableBatch
	{
		public VirtualDesktopStableBatch(long providerEpoch, long snapshotRevision, Guid? currentDesktopId, VirtualDesktopReadStatus currentDesktopReadStatus, IEnumerable<VirtualDesktopStableEntry> desktops, VirtualDesktopStableReason reason)
		{
			if (currentDesktopReadStatus == VirtualDesktopReadStatus.Success && !currentDesktopId.HasValue) throw new ArgumentNullException(nameof(currentDesktopId));

			this.ProviderEpoch = providerEpoch;
			this.SnapshotRevision = snapshotRevision;
			this.CurrentDesktopId = currentDesktopId;
			this.CurrentDesktopReadStatus = currentDesktopReadStatus;
			this.Desktops = new ReadOnlyCollection<VirtualDesktopStableEntry>(new List<VirtualDesktopStableEntry>(desktops));
			this.Reason = reason;
		}

		public long ProviderEpoch { get; }
		public long SnapshotRevision { get; }
		public Guid? CurrentDesktopId { get; }
		public VirtualDesktopReadStatus CurrentDesktopReadStatus { get; }
		public IReadOnlyList<VirtualDesktopStableEntry> Desktops { get; }
		public VirtualDesktopStableReason Reason { get; }
	}

	public sealed class VirtualDesktopCurrentTransition
	{
		public VirtualDesktopCurrentTransition(long providerEpoch, long ingressSequence, long baseSnapshotRevision, Guid currentDesktopId)
		{
			this.ProviderEpoch = providerEpoch;
			this.IngressSequence = ingressSequence;
			this.BaseSnapshotRevision = baseSnapshotRevision;
			this.CurrentDesktopId = currentDesktopId;
		}

		public long ProviderEpoch { get; }
		public long IngressSequence { get; }
		public long BaseSnapshotRevision { get; }
		public Guid CurrentDesktopId { get; }
	}

	public enum VirtualDesktopReconciliationStatus
	{
		Succeeded,
		SupersededByReset,
		Cancelled,
		ShuttingDown,
		Unavailable,
	}

	public enum VirtualDesktopProviderFailureCategory
	{
		Unknown,
		StructuralSnapshot,
		PropertySnapshot,
		Scheduler,
		CallbackMaterialization,
		Subscriber,
		ProviderReset,
	}

	public sealed class VirtualDesktopReconciliationResult
	{
		private VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus status, VirtualDesktopStableBatch batch, long? newProviderEpoch, VirtualDesktopProviderFailureCategory? failureCategory)
		{
			this.Status = status;
			this.Batch = batch;
			this.NewProviderEpoch = newProviderEpoch;
			this.FailureCategory = failureCategory;
		}

		public VirtualDesktopReconciliationStatus Status { get; }
		public VirtualDesktopStableBatch Batch { get; }
		public long? NewProviderEpoch { get; }
		public VirtualDesktopProviderFailureCategory? FailureCategory { get; }

		public static VirtualDesktopReconciliationResult Succeeded(VirtualDesktopStableBatch batch) => new VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus.Succeeded, batch, null, null);
		public static VirtualDesktopReconciliationResult SupersededByReset(long newProviderEpoch) => new VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus.SupersededByReset, null, newProviderEpoch, null);
		public static VirtualDesktopReconciliationResult Cancelled() => new VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus.Cancelled, null, null, null);
		public static VirtualDesktopReconciliationResult ShuttingDown() => new VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus.ShuttingDown, null, null, null);
		public static VirtualDesktopReconciliationResult Unavailable(VirtualDesktopProviderFailureCategory failureCategory) => new VirtualDesktopReconciliationResult(VirtualDesktopReconciliationStatus.Unavailable, null, null, failureCategory);
	}

	public enum VirtualDesktopProviderFaultPhase
	{
		CallbackMaterialization,
		Scheduling,
		SnapshotCapture,
		MirrorTransition,
		EventDispatch,
		Shutdown,
	}

	public enum VirtualDesktopProviderEventKind
	{
		Unknown,
		Created,
		DestroyBegin,
		DestroyFailed,
		Destroyed,
		Moved,
		ApplicationViewChanged,
		Renamed,
		WallpaperChanged,
		CurrentChanged,
		DesktopSwitched,
		RemoteDesktopConnected,
		PropertyChanging,
		PropertyChanged,
		StableBatch,
		CurrentTransition,
	}

	public sealed class VirtualDesktopProviderFault
	{
		public VirtualDesktopProviderFault(VirtualDesktopProviderFaultPhase phase, VirtualDesktopProviderEventKind eventKind, Guid? desktopId, string exceptionType, int? nativeErrorCode, long sequence)
		{
			this.Phase = phase;
			this.EventKind = eventKind;
			this.DesktopId = desktopId;
			this.ExceptionType = exceptionType;
			this.NativeErrorCode = nativeErrorCode;
			this.Sequence = sequence;
		}

		public VirtualDesktopProviderFaultPhase Phase { get; }
		public VirtualDesktopProviderEventKind EventKind { get; }
		public Guid? DesktopId { get; }
		public string ExceptionType { get; }
		public int? NativeErrorCode { get; }
		public long Sequence { get; }
	}
}
