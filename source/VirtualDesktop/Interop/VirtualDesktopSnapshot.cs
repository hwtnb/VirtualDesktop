using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WindowsDesktop.Interop
{
	internal enum VirtualDesktopReadErrorCategory
	{
		None,
		DesktopCollection,
		Count,
		InvalidCount,
		GetAt,
		DesktopObject,
		Identity,
		Property,
		CurrentDesktop,
		Managed,
	}

	internal sealed class VirtualDesktopReadResult<T>
	{
		private VirtualDesktopReadResult(VirtualDesktopReadStatus status, T value, VirtualDesktopReadErrorCategory errorCategory, int? nativeErrorCode)
		{
			this.Status = status;
			this.Value = value;
			this.ErrorCategory = errorCategory;
			this.NativeErrorCode = nativeErrorCode;
		}

		internal VirtualDesktopReadStatus Status { get; }
		internal T Value { get; }
		internal VirtualDesktopReadErrorCategory ErrorCategory { get; }
		internal int? NativeErrorCode { get; }

		internal static VirtualDesktopReadResult<T> Success(T value) => new VirtualDesktopReadResult<T>(VirtualDesktopReadStatus.Success, value, VirtualDesktopReadErrorCategory.None, null);
		internal static VirtualDesktopReadResult<T> Unsupported() => new VirtualDesktopReadResult<T>(VirtualDesktopReadStatus.Unsupported, default(T), VirtualDesktopReadErrorCategory.None, null);
		internal static VirtualDesktopReadResult<T> Failed(VirtualDesktopReadErrorCategory category, int? nativeErrorCode) => new VirtualDesktopReadResult<T>(VirtualDesktopReadStatus.Failed, default(T), category, nativeErrorCode);
		internal static VirtualDesktopReadResult<T> NotAttempted() => new VirtualDesktopReadResult<T>(VirtualDesktopReadStatus.NotAttempted, default(T), VirtualDesktopReadErrorCategory.None, null);
	}

	internal enum VirtualDesktopSnapshotStructuralFailureKind
	{
		DesktopCollectionReadFailed,
		DesktopCollectionUnavailable,
		CountReadFailed,
		InvalidCount,
		GetAtFailed,
		DesktopObjectUnavailable,
		IdReadFailed,
		EmptyId,
		DuplicateId,
		ExpectedCountMismatch,
	}

	internal sealed class VirtualDesktopSnapshotStructuralFailure
	{
		internal VirtualDesktopSnapshotStructuralFailure(VirtualDesktopSnapshotStructuralFailureKind kind, int? requestedIndex, VirtualDesktopReadResult<Guid> id, VirtualDesktopReadResult<string> name, VirtualDesktopReadResult<string> wallpaperPath, int? nativeErrorCode = null, int? expectedCount = null, int? actualCount = null)
		{
			this.Kind = kind;
			this.RequestedIndex = requestedIndex;
			this.Id = id;
			this.Name = name;
			this.WallpaperPath = wallpaperPath;
			this.NativeErrorCode = nativeErrorCode;
			this.ExpectedCount = expectedCount;
			this.ActualCount = actualCount;
		}

		internal VirtualDesktopSnapshotStructuralFailureKind Kind { get; }
		internal int? RequestedIndex { get; }
		internal VirtualDesktopReadResult<Guid> Id { get; }
		internal VirtualDesktopReadResult<string> Name { get; }
		internal VirtualDesktopReadResult<string> WallpaperPath { get; }
		internal int? NativeErrorCode { get; }
		internal int? ExpectedCount { get; }
		internal int? ActualCount { get; }
	}

	internal sealed class VirtualDesktopSnapshotCapabilities
	{
		internal VirtualDesktopSnapshotCapabilities(string interfaceName, bool canEnumerate, bool canReadCurrentDesktopId, bool canReadName, bool canReadWallpaperPath)
		{
			this.InterfaceName = interfaceName;
			this.CanEnumerate = canEnumerate;
			this.CanReadCurrentDesktopId = canReadCurrentDesktopId;
			this.CanReadName = canReadName;
			this.CanReadWallpaperPath = canReadWallpaperPath;
		}

		internal string InterfaceName { get; }
		internal bool CanEnumerate { get; }
		internal bool CanReadCurrentDesktopId { get; }
		internal bool CanReadName { get; }
		internal bool CanReadWallpaperPath { get; }
	}

	internal sealed class VirtualDesktopSnapshotEntry
	{
		internal VirtualDesktopSnapshotEntry(Guid id, int orderIndex, VirtualDesktopReadResult<string> name, VirtualDesktopReadResult<string> wallpaperPath)
		{
			if (name.Status == VirtualDesktopReadStatus.Success && name.Value == null) throw new ArgumentException("A successful name read must contain a non-null string.", nameof(name));
			if (wallpaperPath.Status == VirtualDesktopReadStatus.Success && wallpaperPath.Value == null) throw new ArgumentException("A successful wallpaper path read must contain a non-null string.", nameof(wallpaperPath));

			this.Id = id;
			this.OrderIndex = orderIndex;
			this.Name = name;
			this.WallpaperPath = wallpaperPath;
		}

		internal Guid Id { get; }
		internal int OrderIndex { get; }
		internal VirtualDesktopReadResult<string> Name { get; }
		internal VirtualDesktopReadResult<string> WallpaperPath { get; }
	}

	internal sealed class VirtualDesktopSnapshotBatch
	{
		internal VirtualDesktopSnapshotBatch(IEnumerable<VirtualDesktopSnapshotEntry> desktops, IEnumerable<VirtualDesktopSnapshotStructuralFailure> structuralFailures, VirtualDesktopReadResult<int> enumeration, VirtualDesktopReadResult<Guid> currentDesktopId, VirtualDesktopSnapshotCapabilities capabilities, Guid captureAttemptId)
		{
			this.Desktops = new ReadOnlyCollection<VirtualDesktopSnapshotEntry>(new List<VirtualDesktopSnapshotEntry>(desktops));
			this.StructuralFailures = new ReadOnlyCollection<VirtualDesktopSnapshotStructuralFailure>(new List<VirtualDesktopSnapshotStructuralFailure>(structuralFailures));
			this.Enumeration = enumeration;
			this.CurrentDesktopId = currentDesktopId;
			this.Capabilities = capabilities;
			this.CaptureAttemptId = captureAttemptId;
		}

		internal IReadOnlyList<VirtualDesktopSnapshotEntry> Desktops { get; }
		internal IReadOnlyList<VirtualDesktopSnapshotStructuralFailure> StructuralFailures { get; }
		internal VirtualDesktopReadResult<int> Enumeration { get; }
		internal VirtualDesktopReadResult<Guid> CurrentDesktopId { get; }
		internal VirtualDesktopSnapshotCapabilities Capabilities { get; }
		internal Guid CaptureAttemptId { get; }
	}

	internal interface IVirtualDesktopSnapshotInterfaceCatalog
	{
		Type GetType(string typeName);
	}

	internal sealed class ComAssemblyVirtualDesktopSnapshotInterfaceCatalog : IVirtualDesktopSnapshotInterfaceCatalog
	{
		private readonly ComInterfaceAssembly _assembly;

		internal ComAssemblyVirtualDesktopSnapshotInterfaceCatalog(ComInterfaceAssembly assembly) => this._assembly = assembly;
		public Type GetType(string typeName) => this._assembly.GetType(typeName);
	}

	internal sealed class ResolvedVirtualDesktopSnapshotInterface
	{
		internal ResolvedVirtualDesktopSnapshotInterface(Type interfaceType)
		{
			this.InterfaceType = interfaceType;
			this.HasId = interfaceType.GetMethod("GetID") != null;
			this.HasName = interfaceType.GetMethod("GetName") != null;
			this.HasWallpaperPath = interfaceType.GetMethod("GetWallpaperPath") != null;
		}

		internal Type InterfaceType { get; }
		internal bool HasId { get; }
		internal bool HasName { get; }
		internal bool HasWallpaperPath { get; }
	}

	internal static class VirtualDesktopSnapshotInterfaceResolver
	{
		internal static ResolvedVirtualDesktopSnapshotInterface Resolve(IVirtualDesktopSnapshotInterfaceCatalog catalog)
		{
			var interfaceType = catalog.GetType("IVirtualDesktop2") ?? catalog.GetType("IVirtualDesktop");
			if (interfaceType == null) throw new NotSupportedException("IVirtualDesktop is not resolved.");
			return new ResolvedVirtualDesktopSnapshotInterface(interfaceType);
		}
	}

	internal interface IVirtualDesktopSnapshotSource
	{
		VirtualDesktopSnapshotCapabilities Capabilities { get; }
		IVirtualDesktopSnapshotCollection GetDesktopCollection();
		Guid GetCurrentDesktopId();
	}

	internal interface IVirtualDesktopSnapshotCollection
	{
		int GetCount();
		IVirtualDesktopSnapshotValueReader GetDesktop(int index);
	}

	internal interface IVirtualDesktopSnapshotValueReader
	{
		Guid GetId();
		string GetName();
		string GetWallpaperPath();
	}

	internal static class VirtualDesktopSnapshotReader
	{
		internal static VirtualDesktopSnapshotBatch Capture(IVirtualDesktopSnapshotSource source)
		{
			var desktops = new List<VirtualDesktopSnapshotEntry>();
			var failures = new List<VirtualDesktopSnapshotStructuralFailure>();
			var currentDesktopId = ReadCurrentDesktopId(source);
			var notAttemptedId = VirtualDesktopReadResult<Guid>.NotAttempted();
			var notAttemptedProperty = VirtualDesktopReadResult<string>.NotAttempted();
			IVirtualDesktopSnapshotCollection collection;
			if (!source.Capabilities.CanEnumerate)
			{
				return CreateBatch(desktops, failures, VirtualDesktopReadResult<int>.Unsupported(), currentDesktopId, source.Capabilities);
			}

			try
			{
				collection = source.GetDesktopCollection();
			}
			catch (Exception ex)
			{
				var result = Failure<int>(ex, VirtualDesktopReadErrorCategory.DesktopCollection);
				failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.DesktopCollectionReadFailed, null, notAttemptedId, result.NativeErrorCode));
				return CreateBatch(desktops, failures, result, currentDesktopId, source.Capabilities);
			}

			if (collection == null)
			{
				var result = VirtualDesktopReadResult<int>.Failed(VirtualDesktopReadErrorCategory.DesktopCollection, null);
				failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.DesktopCollectionUnavailable, null, notAttemptedId, null));
				return CreateBatch(desktops, failures, result, currentDesktopId, source.Capabilities);
			}

			int count;
			try
			{
				count = collection.GetCount();
			}
			catch (Exception ex)
			{
				var result = Failure<int>(ex, VirtualDesktopReadErrorCategory.Count);
				failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.CountReadFailed, null, notAttemptedId, result.NativeErrorCode));
				return CreateBatch(desktops, failures, result, currentDesktopId, source.Capabilities);
			}

			if (count < 0)
			{
				var result = VirtualDesktopReadResult<int>.Failed(VirtualDesktopReadErrorCategory.InvalidCount, null);
				failures.Add(new VirtualDesktopSnapshotStructuralFailure(VirtualDesktopSnapshotStructuralFailureKind.InvalidCount, null, notAttemptedId, notAttemptedProperty, notAttemptedProperty, expectedCount: 0, actualCount: count));
				return CreateBatch(desktops, failures, result, currentDesktopId, source.Capabilities);
			}

			var ids = new HashSet<Guid>();
			for (var requestedIndex = 0; requestedIndex < count; requestedIndex++)
			{
				IVirtualDesktopSnapshotValueReader reader;
				try
				{
					reader = collection.GetDesktop(requestedIndex);
				}
				catch (Exception ex)
				{
					var id = Failure<Guid>(ex, VirtualDesktopReadErrorCategory.GetAt);
					failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.GetAtFailed, requestedIndex, id, id.NativeErrorCode));
					continue;
				}

				if (reader == null)
				{
					failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.DesktopObjectUnavailable, requestedIndex, VirtualDesktopReadResult<Guid>.Failed(VirtualDesktopReadErrorCategory.DesktopObject, null), null));
					continue;
				}

				Guid idValue;
				try
				{
					idValue = reader.GetId();
				}
				catch (Exception ex)
				{
					var id = Failure<Guid>(ex, VirtualDesktopReadErrorCategory.Identity);
					failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.IdReadFailed, requestedIndex, id, id.NativeErrorCode));
					continue;
				}

				if (idValue == Guid.Empty)
				{
					failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.EmptyId, requestedIndex, VirtualDesktopReadResult<Guid>.Failed(VirtualDesktopReadErrorCategory.Identity, null), null));
					continue;
				}
				if (!ids.Add(idValue))
				{
					failures.Add(Failure(VirtualDesktopSnapshotStructuralFailureKind.DuplicateId, requestedIndex, VirtualDesktopReadResult<Guid>.Failed(VirtualDesktopReadErrorCategory.Identity, null), null));
					continue;
				}

				var name = ReadProperty(source.Capabilities.CanReadName, reader.GetName);
				var wallpaperPath = ReadProperty(source.Capabilities.CanReadWallpaperPath, reader.GetWallpaperPath);
				desktops.Add(new VirtualDesktopSnapshotEntry(idValue, requestedIndex, name, wallpaperPath));
			}

			if (desktops.Count != count) failures.Add(new VirtualDesktopSnapshotStructuralFailure(VirtualDesktopSnapshotStructuralFailureKind.ExpectedCountMismatch, null, notAttemptedId, notAttemptedProperty, notAttemptedProperty, expectedCount: count, actualCount: desktops.Count));

			return CreateBatch(desktops, failures, VirtualDesktopReadResult<int>.Success(count), currentDesktopId, source.Capabilities);
		}

		private static VirtualDesktopSnapshotStructuralFailure Failure(VirtualDesktopSnapshotStructuralFailureKind kind, int? requestedIndex, VirtualDesktopReadResult<Guid> id, int? nativeErrorCode)
			=> new VirtualDesktopSnapshotStructuralFailure(kind, requestedIndex, id, VirtualDesktopReadResult<string>.NotAttempted(), VirtualDesktopReadResult<string>.NotAttempted(), nativeErrorCode);

		private static VirtualDesktopReadResult<Guid> ReadCurrentDesktopId(IVirtualDesktopSnapshotSource source)
		{
			if (!source.Capabilities.CanReadCurrentDesktopId) return VirtualDesktopReadResult<Guid>.Unsupported();
			try
			{
				var id = source.GetCurrentDesktopId();
				return id == Guid.Empty ? VirtualDesktopReadResult<Guid>.Failed(VirtualDesktopReadErrorCategory.CurrentDesktop, null) : VirtualDesktopReadResult<Guid>.Success(id);
			}
			catch (Exception ex)
			{
				return Failure<Guid>(ex, VirtualDesktopReadErrorCategory.CurrentDesktop);
			}
		}

		private static VirtualDesktopReadResult<string> ReadProperty(bool supported, Func<string> getter)
		{
			if (!supported) return VirtualDesktopReadResult<string>.Unsupported();
			try
			{
				return VirtualDesktopReadResult<string>.Success(getter() ?? string.Empty);
			}
			catch (Exception ex)
			{
				return Failure<string>(ex, VirtualDesktopReadErrorCategory.Property);
			}
		}

		private static VirtualDesktopSnapshotBatch CreateBatch(IEnumerable<VirtualDesktopSnapshotEntry> desktops, IEnumerable<VirtualDesktopSnapshotStructuralFailure> failures, VirtualDesktopReadResult<int> enumeration, VirtualDesktopReadResult<Guid> currentDesktopId, VirtualDesktopSnapshotCapabilities capabilities)
			=> new VirtualDesktopSnapshotBatch(desktops, failures, enumeration, currentDesktopId, capabilities, Guid.NewGuid());

		private static VirtualDesktopReadResult<T> Failure<T>(Exception exception, VirtualDesktopReadErrorCategory category)
		{
			var current = exception;
			while (current is TargetInvocationException && current.InnerException != null) current = current.InnerException;
			return VirtualDesktopReadResult<T>.Failed(category, (current as COMException)?.HResult);
		}
	}

	internal interface IVirtualDesktopSnapshotManagerInvocation
	{
		Type InterfaceType { get; }
		object Invoke(string methodName, object[] parameters);
	}

	internal sealed class VirtualDesktopManagerSnapshotInvocation : IVirtualDesktopSnapshotManagerInvocation
	{
		private readonly VirtualDesktopManagerInternal _manager;

		internal VirtualDesktopManagerSnapshotInvocation(VirtualDesktopManagerInternal manager) => this._manager = manager;
		public Type InterfaceType => this._manager.SnapshotManagerType;
		public object Invoke(string methodName, object[] parameters) => this._manager.InvokeSnapshotMember(methodName, parameters);
	}

	internal interface IVirtualDesktopSnapshotObjectArray
	{
		uint GetCount();
		object GetAt(uint index, Guid interfaceId);
	}

	internal sealed class ComVirtualDesktopSnapshotObjectArray : IVirtualDesktopSnapshotObjectArray
	{
		private readonly IObjectArray _array;

		internal ComVirtualDesktopSnapshotObjectArray(IObjectArray array) => this._array = array;
		public uint GetCount() => this._array.GetCount();

		public object GetAt(uint index, Guid interfaceId)
		{
			this._array.GetAt(index, ref interfaceId, out var comObject);
			return comObject;
		}
	}

	internal sealed class DynamicComVirtualDesktopSnapshotSource : IVirtualDesktopSnapshotSource
	{
		private readonly IVirtualDesktopSnapshotManagerInvocation _manager;
		private readonly ResolvedVirtualDesktopSnapshotInterface _desktopInterface;

		internal DynamicComVirtualDesktopSnapshotSource(VirtualDesktopManagerInternal manager)
			: this(new VirtualDesktopManagerSnapshotInvocation(manager), new ComAssemblyVirtualDesktopSnapshotInterfaceCatalog(manager.SnapshotAssembly))
		{
		}

		internal DynamicComVirtualDesktopSnapshotSource(IVirtualDesktopSnapshotManagerInvocation manager, IVirtualDesktopSnapshotInterfaceCatalog interfaceCatalog)
		{
			this._manager = manager;
			this._desktopInterface = VirtualDesktopSnapshotInterfaceResolver.Resolve(interfaceCatalog);
			this.Capabilities = new VirtualDesktopSnapshotCapabilities(
				this._desktopInterface.InterfaceType.Name,
				manager.InterfaceType.GetMethod("GetDesktops") != null,
				manager.InterfaceType.GetMethod("GetCurrentDesktop") != null,
				this._desktopInterface.HasName,
				this._desktopInterface.HasWallpaperPath);
		}

		public VirtualDesktopSnapshotCapabilities Capabilities { get; }

		public IVirtualDesktopSnapshotCollection GetDesktopCollection()
		{
			if (!this.Capabilities.CanEnumerate) throw new NotSupportedException("GetDesktops is not supported.");
			var rawArray = this.InvokeManager("GetDesktops");
			if (rawArray == null) return null;
			var array = rawArray as IVirtualDesktopSnapshotObjectArray
				?? new ComVirtualDesktopSnapshotObjectArray((IObjectArray)rawArray);
			return new DynamicComVirtualDesktopSnapshotCollection(array, this._desktopInterface.InterfaceType);
		}

		public Guid GetCurrentDesktopId()
		{
			if (!this.Capabilities.CanReadCurrentDesktopId) throw new NotSupportedException("GetCurrentDesktop is not supported.");
			var comObject = this.InvokeManager("GetCurrentDesktop");
			return comObject == null ? Guid.Empty : new DynamicComVirtualDesktopSnapshotValueReader(this._desktopInterface.InterfaceType, comObject).GetId();
		}

		private object InvokeManager(string methodName)
		{
			var method = this._manager.InterfaceType.GetMethod(methodName);
			if (method == null) throw new NotSupportedException(methodName + " is not supported.");
			var parameters = method.GetParameters().Length == 0 ? null : new object[] { IntPtr.Zero };
			return this._manager.Invoke(methodName, parameters);
		}
	}

	internal sealed class DynamicComVirtualDesktopSnapshotCollection : IVirtualDesktopSnapshotCollection
	{
		private readonly IVirtualDesktopSnapshotObjectArray _array;
		private readonly Type _desktopType;

		internal DynamicComVirtualDesktopSnapshotCollection(IVirtualDesktopSnapshotObjectArray array, Type desktopType)
		{
			this._array = array;
			this._desktopType = desktopType;
		}

		public int GetCount() => checked((int)this._array.GetCount());

		public IVirtualDesktopSnapshotValueReader GetDesktop(int index)
		{
			var comObject = this._array.GetAt(checked((uint)index), this._desktopType.GUID);
			return comObject == null ? null : new DynamicComVirtualDesktopSnapshotValueReader(this._desktopType, comObject);
		}
	}

	internal sealed class DynamicComVirtualDesktopSnapshotValueReader : IVirtualDesktopSnapshotValueReader
	{
		private readonly Type _desktopType;
		private readonly object _comObject;

		internal DynamicComVirtualDesktopSnapshotValueReader(Type desktopType, object comObject)
		{
			this._desktopType = desktopType;
			this._comObject = comObject;
		}

		public Guid GetId() => (Guid)this.Invoke("GetID");
		public string GetName() => this.InvokeString("GetName");
		public string GetWallpaperPath() => this.InvokeString("GetWallpaperPath");

		private string InvokeString(string methodName)
		{
			var value = this.Invoke(methodName);
			if (value == null) return null;
			return (HString)value;
		}

		private object Invoke(string methodName)
		{
			var method = this._desktopType.GetMethod(methodName);
			if (method == null) throw new NotSupportedException(methodName + " is not supported.");
			try
			{
				return method.Invoke(this._comObject, null);
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				throw ex.InnerException;
			}
		}
	}
}
