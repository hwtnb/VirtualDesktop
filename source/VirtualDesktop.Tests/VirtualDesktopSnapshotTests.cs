using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop;
using Xunit;

namespace WindowsDesktop.Tests
{
	public class VirtualDesktopSnapshotTests
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

		[Theory]
		[InlineData("10240-basic", false, false)]
		[InlineData("10240-IVirtualDesktop2", true, false)]
		[InlineData("20231", true, false)]
		[InlineData("21313", true, true)]
		[InlineData("26100", true, true)]
		public void ResolverUsesResolvedInterfaceCapabilityMatrix(string variant, bool expectedName, bool expectedWallpaper)
		{
			var catalog = new FakeCatalog();
			if (variant == "10240-basic") catalog.Basic = typeof(BasicDesktop);
			if (variant == "10240-IVirtualDesktop2") { catalog.Basic = typeof(BasicDesktop); catalog.Version2 = typeof(NameDesktop); }
			if (variant == "20231") catalog.Basic = typeof(NameDesktop);
			if (variant == "21313" || variant == "26100") catalog.Basic = typeof(NameWallpaperDesktop);

			var resolved = VirtualDesktopSnapshotInterfaceResolver.Resolve(catalog);

			Assert.True(resolved.HasId);
			Assert.Equal(expectedName, resolved.HasName);
			Assert.Equal(expectedWallpaper, resolved.HasWallpaperPath);
			Assert.Same(catalog.Version2 ?? catalog.Basic, resolved.InterfaceType);
		}

		[Theory]
		[InlineData(false, false)]
		[InlineData(false, true)]
		[InlineData(true, false)]
		[InlineData(true, true)]
		public void ProductionRawSourceBypassesWrapperAndUsesResolvedInterface(bool useNameInterface, bool useMonitorParameter)
		{
			var desktop = new FakeDynamicDesktop("raw-name", "raw-wallpaper");
			var array = new FakeObjectArray(desktop);
			var manager = new FakeManagerInvocation(useMonitorParameter ? typeof(ManagerWithMonitor) : typeof(ManagerWithoutMonitor), array, desktop);
			var catalog = new FakeCatalog { Basic = typeof(BasicDesktop), Version2 = useNameInterface ? typeof(NameDesktop) : null };
			var source = new DynamicComVirtualDesktopSnapshotSource(manager, catalog);
			Assert.Equal("cached", desktop.ReadWrapperName());
			var wrapperReads = desktop.WrapperNameCalls;

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Equal(wrapperReads, desktop.WrapperNameCalls);
			Assert.Equal(2, desktop.IdCalls);
			Assert.Equal(useNameInterface ? 1 : 0, desktop.NameCalls);
			Assert.Equal(0, desktop.WallpaperCalls);
			Assert.Equal(useNameInterface ? "raw-name" : null, batch.Desktops[0].Name.Value);
			Assert.Equal(useNameInterface ? VirtualDesktopReadStatus.Success : VirtualDesktopReadStatus.Unsupported, batch.Desktops[0].Name.Status);
			Assert.Equal(1, array.GetAtCalls);
			Assert.Equal(0u, array.RequestedIndexes.Single());
			Assert.Equal((useNameInterface ? typeof(NameDesktop) : typeof(BasicDesktop)).GUID, array.RequestedInterfaceIds.Single());
			Assert.Equal(2, manager.Calls.Count);
			Assert.All(manager.Calls, call => Assert.Equal(useMonitorParameter ? 1 : 0, call.Parameters?.Length ?? 0));
			if (useMonitorParameter) Assert.All(manager.Calls, call => Assert.Equal(IntPtr.Zero, call.Parameters[0]));
		}

		[Fact]
		public void CapturesIdentityOrderPropertiesAndCurrentDesktop()
		{
			var desktop = FakeDesktop.Success("name", "wallpaper");
			var source = FakeSource.With(desktop);
			source.CurrentDesktopId = desktop.Id;

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Equal(desktop.Id, batch.Desktops[0].Id);
			Assert.Equal(0, batch.Desktops[0].OrderIndex);
			Assert.Equal("name", batch.Desktops[0].Name.Value);
			Assert.Equal("wallpaper", batch.Desktops[0].WallpaperPath.Value);
			Assert.Equal(desktop.Id, batch.CurrentDesktopId.Value);
			Assert.Equal(VirtualDesktopReadStatus.Success, batch.Enumeration.Status);
			Assert.NotEqual(Guid.Empty, batch.CaptureAttemptId);
		}

		[Fact]
		public void EmptyUnsupportedFailedAndNotAttemptedRemainDistinct()
		{
			var empty = FakeDesktop.Success(string.Empty, string.Empty);
			var failed = FakeDesktop.Success("unused", "ok");
			failed.NameError = new COMException("synthetic", unchecked((int)0x80004005));
			var supported = VirtualDesktopSnapshotReader.Capture(FakeSource.With(empty, failed));
			var unsupportedDesktop = FakeDesktop.Success("unused", "unused");
			var unsupported = VirtualDesktopSnapshotReader.Capture(FakeSource.WithCapabilities(false, false, unsupportedDesktop));
			var idFailure = FakeDesktop.Success("never", "never");
			idFailure.IdError = new COMException("synthetic", 88);
			var structural = VirtualDesktopSnapshotReader.Capture(FakeSource.With(idFailure));

			Assert.Equal(VirtualDesktopReadStatus.Success, supported.Desktops[0].Name.Status);
			Assert.Equal(string.Empty, supported.Desktops[0].Name.Value);
			Assert.Equal(VirtualDesktopReadStatus.Failed, supported.Desktops[1].Name.Status);
			Assert.Equal(unchecked((int)0x80004005), supported.Desktops[1].Name.NativeErrorCode);
			Assert.Equal(VirtualDesktopReadStatus.Unsupported, unsupported.Desktops[0].Name.Status);
			Assert.Equal(VirtualDesktopReadStatus.Unsupported, unsupported.Desktops[0].WallpaperPath.Status);
			Assert.Equal(0, unsupportedDesktop.NameCalls);
			Assert.Equal(VirtualDesktopReadStatus.NotAttempted, structural.StructuralFailures.Single(x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.IdReadFailed).Name.Status);
		}

		[Fact]
		public void ProductionHStringNullsNormalizeToConfirmedEmptyIndependently()
		{
			var nameNull = new FakeDynamicDesktop(null, "wall-a");
			var wallpaperNull = new FakeDynamicDesktop("name-b", null);
			var array = new FakeObjectArray(nameNull, wallpaperNull);
			var manager = new FakeManagerInvocation(typeof(ManagerWithoutMonitor), array, nameNull);
			var source = new DynamicComVirtualDesktopSnapshotSource(manager, new FakeCatalog { Basic = typeof(NameWallpaperDesktop) });

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Equal(VirtualDesktopReadStatus.Success, batch.Desktops[0].Name.Status);
			Assert.Equal(string.Empty, batch.Desktops[0].Name.Value);
			Assert.Equal("wall-a", batch.Desktops[0].WallpaperPath.Value);
			Assert.Equal("name-b", batch.Desktops[1].Name.Value);
			Assert.Equal(VirtualDesktopReadStatus.Success, batch.Desktops[1].WallpaperPath.Status);
			Assert.Equal(string.Empty, batch.Desktops[1].WallpaperPath.Value);
			Assert.Equal(2, nameNull.NameCalls + wallpaperNull.NameCalls);
			Assert.Equal(2, nameNull.WallpaperCalls + wallpaperNull.WallpaperCalls);
		}

		[Fact]
		public void PropertyFailuresAreIsolated()
		{
			var nameFails = FakeDesktop.Success("unused", "wall-a");
			nameFails.NameError = new InvalidOperationException("synthetic");
			var wallpaperFails = FakeDesktop.Success("name-b", "unused");
			wallpaperFails.WallpaperError = new InvalidOperationException("synthetic");

			var batch = VirtualDesktopSnapshotReader.Capture(FakeSource.With(nameFails, wallpaperFails));

			Assert.Equal(VirtualDesktopReadStatus.Failed, batch.Desktops[0].Name.Status);
			Assert.Equal("wall-a", batch.Desktops[0].WallpaperPath.Value);
			Assert.Equal("name-b", batch.Desktops[1].Name.Value);
			Assert.Equal(VirtualDesktopReadStatus.Failed, batch.Desktops[1].WallpaperPath.Status);
		}

		[Fact]
		public void CollectionFailureDoesNotHideCurrentDesktopResult()
		{
			var current = Guid.NewGuid();
			var source = FakeSource.With();
			source.CurrentDesktopId = current;
			source.CollectionError = new COMException("synthetic", 11);

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Equal(VirtualDesktopReadStatus.Failed, batch.Enumeration.Status);
			Assert.Equal(VirtualDesktopReadErrorCategory.DesktopCollection, batch.Enumeration.ErrorCategory);
			Assert.Equal(VirtualDesktopReadStatus.Success, batch.CurrentDesktopId.Status);
			Assert.Equal(current, batch.CurrentDesktopId.Value);
			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.DesktopCollectionReadFailed);
		}

		[Fact]
		public void CurrentDesktopFailureDoesNotInvalidateCompleteTopology()
		{
			var source = FakeSource.With(FakeDesktop.Success("a", "b"));
			source.CurrentError = new COMException("synthetic", 12);

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Single(batch.Desktops);
			Assert.Empty(batch.StructuralFailures);
			Assert.Equal(VirtualDesktopReadStatus.Success, batch.Enumeration.Status);
			Assert.Equal(VirtualDesktopReadStatus.Failed, batch.CurrentDesktopId.Status);
			Assert.Equal(12, batch.CurrentDesktopId.NativeErrorCode);
		}

		[Fact]
		public void UnresolvedEnumerationAndCurrentMethodsAreUnsupportedWithoutCalls()
		{
			var source = FakeSource.WithCapabilities(false, false, false, false, FakeDesktop.Success("unused", "unused"));

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Equal(VirtualDesktopReadStatus.Unsupported, batch.Enumeration.Status);
			Assert.Equal(VirtualDesktopReadStatus.Unsupported, batch.CurrentDesktopId.Status);
			Assert.Equal(0, source.CollectionCalls);
			Assert.Equal(0, source.CurrentCalls);
			Assert.Empty(batch.Desktops);
		}

		[Fact]
		public void CountFailuresAndInvalidCountsAreSeparate()
		{
			var failed = FakeSource.With();
			failed.Collection.CountError = new COMException("synthetic", 21);
			var invalid = FakeSource.With();
			invalid.Collection.CountOverride = -1;

			var failedBatch = VirtualDesktopSnapshotReader.Capture(failed);
			var invalidBatch = VirtualDesktopSnapshotReader.Capture(invalid);

			Assert.Equal(VirtualDesktopReadErrorCategory.Count, failedBatch.Enumeration.ErrorCategory);
			Assert.Contains(failedBatch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.CountReadFailed);
			Assert.Equal(VirtualDesktopReadErrorCategory.InvalidCount, invalidBatch.Enumeration.ErrorCategory);
			Assert.Contains(invalidBatch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.InvalidCount);
		}

		[Fact]
		public void GetAtAndUnavailableObjectAreClassifiedAndRemainderContinues()
		{
			var source = FakeSource.With(FakeDesktop.Success("first", "one"), FakeDesktop.Success("second", "two"), FakeDesktop.Success("third", "three"));
			source.Collection.GetErrors[0] = new COMException("synthetic", 31);
			source.Collection.NullIndexes.Add(1);

			var batch = VirtualDesktopSnapshotReader.Capture(source);

			Assert.Single(batch.Desktops);
			Assert.Equal(2, batch.Desktops[0].OrderIndex);
			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.GetAtFailed);
			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.DesktopObjectUnavailable);
			var mismatch = Assert.Single(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.ExpectedCountMismatch);
			Assert.Equal(3, mismatch.ExpectedCount);
			Assert.Equal(1, mismatch.ActualCount);
		}

		[Fact]
		public void IdFailuresEmptyAndDuplicateIdsAreSeparate()
		{
			var idFailure = FakeDesktop.Success("a", "a");
			idFailure.IdError = new COMException("synthetic", 41);
			var empty = FakeDesktop.Success("b", "b");
			empty.Id = Guid.Empty;
			var duplicateId = Guid.NewGuid();
			var duplicateA = FakeDesktop.Success("c", "c");
			duplicateA.Id = duplicateId;
			var duplicateB = FakeDesktop.Success("d", "d");
			duplicateB.Id = duplicateId;

			var batch = VirtualDesktopSnapshotReader.Capture(FakeSource.With(idFailure, empty, duplicateA, duplicateB));

			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.IdReadFailed);
			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.EmptyId);
			Assert.Contains(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.DuplicateId);
			Assert.Single(batch.Desktops);
			var mismatch = Assert.Single(batch.StructuralFailures, x => x.Kind == VirtualDesktopSnapshotStructuralFailureKind.ExpectedCountMismatch);
			Assert.Equal(4, mismatch.ExpectedCount);
			Assert.Equal(1, mismatch.ActualCount);
		}

		[Fact]
		public void RequestedCollectionIndexIsAuthoritativeOrder()
		{
			var first = FakeDesktop.Success("a", "a");
			var second = FakeDesktop.Success("b", "b");

			var batch = VirtualDesktopSnapshotReader.Capture(FakeSource.With(first, second));

			Assert.Equal(new[] { 0, 1 }, batch.Desktops.Select(x => x.OrderIndex));
			Assert.Empty(batch.StructuralFailures);
		}

		[Fact]
		public void DtoOutlivesSourceLifetime()
		{
			var desktop = FakeDesktop.Success("detached-name", "detached-wallpaper");
			var source = FakeSource.With(desktop);
			var batch = VirtualDesktopSnapshotReader.Capture(source);
			source.Alive = false;
			desktop.Alive = false;

			Assert.Equal("detached-name", batch.Desktops[0].Name.Value);
			Assert.Equal("detached-wallpaper", batch.Desktops[0].WallpaperPath.Value);
			Assert.IsType<string>(batch.Desktops[0].Name.Value);
		}

		[Fact]
		public void NewSourceCaptureDoesNotDependOnOldEntry()
		{
			var firstSource = FakeSource.With(FakeDesktop.Success("before", "wall-before"));
			var first = VirtualDesktopSnapshotReader.Capture(firstSource);
			firstSource.Alive = false;
			var secondSource = FakeSource.With(FakeDesktop.Success("after", "wall-after"));
			var second = VirtualDesktopSnapshotReader.Capture(secondSource);

			Assert.Equal("before", first.Desktops[0].Name.Value);
			Assert.Equal("after", second.Desktops[0].Name.Value);
			Assert.NotEqual(first.CaptureAttemptId, second.CaptureAttemptId);
		}

		[Fact]
		public void RecreatedProductionSourceUsesFreshManagerAndRawObjects()
		{
			var firstDesktop = new FakeDynamicDesktop("before", "wall-before");
			var firstSource = CreateProductionSource(new FakeObjectArray(firstDesktop), firstDesktop);
			var first = VirtualDesktopSnapshotReader.Capture(firstSource);
			var secondDesktop = new FakeDynamicDesktop("after", "wall-after");
			var secondSource = CreateProductionSource(new FakeObjectArray(secondDesktop), secondDesktop);
			var second = VirtualDesktopSnapshotReader.Capture(secondSource);

			Assert.Equal("before", first.Desktops[0].Name.Value);
			Assert.Equal("after", second.Desktops[0].Name.Value);
			Assert.Equal(2, firstDesktop.IdCalls);
			Assert.Equal(2, secondDesktop.IdCalls);
			Assert.NotEqual(first.CaptureAttemptId, second.CaptureAttemptId);
		}

		[Fact]
		public void PublicDtosCopyCollectionsAndExposeNoInteropObjects()
		{
			var entries = new List<VirtualDesktopStableEntry> { new VirtualDesktopStableEntry(Guid.NewGuid(), 0, string.Empty, VirtualDesktopReadStatus.Success, null, VirtualDesktopReadStatus.Unsupported) };
			var batch = new VirtualDesktopStableBatch(1, 2, null, VirtualDesktopReadStatus.Failed, entries, VirtualDesktopStableReason.Initialization);
			entries.Clear();

			Assert.Single(batch.Desktops);
			Assert.Throws<ArgumentNullException>(() => new VirtualDesktopStableEntry(Guid.NewGuid(), 0, null, VirtualDesktopReadStatus.Success, string.Empty, VirtualDesktopReadStatus.Success));
			Assert.Throws<ArgumentNullException>(() => new VirtualDesktopStableEntry(Guid.NewGuid(), 0, string.Empty, VirtualDesktopReadStatus.Success, null, VirtualDesktopReadStatus.Success));
			Assert.Throws<ArgumentNullException>(() => new VirtualDesktopStableBatch(1, 2, null, VirtualDesktopReadStatus.Success, entries, VirtualDesktopStableReason.Initialization));
			Assert.Throws<ArgumentException>(() => new VirtualDesktopSnapshotEntry(Guid.NewGuid(), 0, VirtualDesktopReadResult<string>.Success(null), VirtualDesktopReadResult<string>.Success(string.Empty)));
			var unknown = new VirtualDesktopStableEntry(Guid.NewGuid(), 0, null, VirtualDesktopReadStatus.Failed, null, VirtualDesktopReadStatus.NotAttempted);
			Assert.Null(unknown.Name);
			Assert.Null(unknown.WallpaperPath);
			var forbidden = new[] { typeof(Exception), typeof(Delegate), typeof(MemberInfo) };
			var publicDtoTypes = new[] { typeof(VirtualDesktopStableBatch), typeof(VirtualDesktopStableEntry), typeof(VirtualDesktopCurrentTransition), typeof(VirtualDesktopReconciliationResult), typeof(VirtualDesktopProviderFault) };
			Assert.All(publicDtoTypes.SelectMany(x => x.GetProperties()), property => Assert.DoesNotContain(forbidden, type => type.IsAssignableFrom(property.PropertyType)));
		}

		private static DynamicComVirtualDesktopSnapshotSource CreateProductionSource(FakeObjectArray array, FakeDynamicDesktop currentDesktop)
			=> new DynamicComVirtualDesktopSnapshotSource(
				new FakeManagerInvocation(typeof(ManagerWithoutMonitor), array, currentDesktop),
				new FakeCatalog { Basic = typeof(NameWallpaperDesktop) });

		[Guid("11111111-1111-1111-1111-111111111111")]
		private interface BasicDesktop { Guid GetID(); }

		[Guid("22222222-2222-2222-2222-222222222222")]
		private interface NameDesktop { Guid GetID(); HString GetName(); }

		[Guid("33333333-3333-3333-3333-333333333333")]
		private interface NameWallpaperDesktop { Guid GetID(); HString GetName(); HString GetWallpaperPath(); }

		private interface ManagerWithoutMonitor
		{
			object GetDesktops();
			object GetCurrentDesktop();
		}

		private interface ManagerWithMonitor
		{
			object GetDesktops(IntPtr monitor);
			object GetCurrentDesktop(IntPtr monitor);
		}

		private sealed class FakeCatalog : IVirtualDesktopSnapshotInterfaceCatalog
		{
			internal Type Basic { get; set; }
			internal Type Version2 { get; set; }
			public Type GetType(string typeName) => typeName == "IVirtualDesktop2" ? this.Version2 : this.Basic;
		}

		private sealed class FakeManagerInvocation : IVirtualDesktopSnapshotManagerInvocation
		{
			private readonly object _array;
			private readonly object _currentDesktop;

			internal FakeManagerInvocation(Type interfaceType, object array, object currentDesktop)
			{
				this.InterfaceType = interfaceType;
				this._array = array;
				this._currentDesktop = currentDesktop;
			}

			public Type InterfaceType { get; }
			internal List<ManagerCall> Calls { get; } = new List<ManagerCall>();

			public object Invoke(string methodName, object[] parameters)
			{
				this.Calls.Add(new ManagerCall(methodName, parameters));
				if (methodName == "GetDesktops") return this._array;
				if (methodName == "GetCurrentDesktop") return this._currentDesktop;
				throw new MissingMethodException(methodName);
			}
		}

		private sealed class ManagerCall
		{
			internal ManagerCall(string methodName, object[] parameters)
			{
				this.MethodName = methodName;
				this.Parameters = parameters;
			}

			internal string MethodName { get; }
			internal object[] Parameters { get; }
		}

		private sealed class FakeObjectArray : IObjectArray
		{
			private readonly List<object> _desktops;

			internal FakeObjectArray(params object[] desktops) => this._desktops = new List<object>(desktops);
			internal int GetAtCalls { get; private set; }
			internal List<uint> RequestedIndexes { get; } = new List<uint>();
			internal List<Guid> RequestedInterfaceIds { get; } = new List<Guid>();

			public uint GetCount() => checked((uint)this._desktops.Count);

			public object GetAt(uint index, ref Guid interfaceId, out object comObject)
			{
				this.GetAtCalls++;
				this.RequestedIndexes.Add(index);
				this.RequestedInterfaceIds.Add(interfaceId);
				comObject = this._desktops[checked((int)index)];
				return null;
			}
		}

		private sealed class FakeDynamicDesktop : BasicDesktop, NameDesktop, NameWallpaperDesktop
		{
			private readonly string _name;
			private readonly string _wallpaperPath;

			internal FakeDynamicDesktop(string name, string wallpaperPath)
			{
				this.Id = Guid.NewGuid();
				this._name = name;
				this._wallpaperPath = wallpaperPath;
			}

			internal Guid Id { get; }
			internal int IdCalls { get; private set; }
			internal int NameCalls { get; private set; }
			internal int WallpaperCalls { get; private set; }
			internal int WrapperNameCalls { get; private set; }

			public Guid GetID()
			{
				this.IdCalls++;
				return this.Id;
			}

			public HString GetName()
			{
				this.NameCalls++;
				return this._name == null ? default(HString) : HString.FromManagedOwned(this._name);
			}

			public HString GetWallpaperPath()
			{
				this.WallpaperCalls++;
				return this._wallpaperPath == null ? default(HString) : HString.FromManagedOwned(this._wallpaperPath);
			}

			internal string ReadWrapperName()
			{
				this.WrapperNameCalls++;
				return "cached";
			}
		}

		private sealed class FakeSource : IVirtualDesktopSnapshotSource
		{
			private FakeSource(VirtualDesktopSnapshotCapabilities capabilities, params FakeDesktop[] desktops)
			{
				this.Capabilities = capabilities;
				this.Collection = new FakeCollection(desktops);
				this.CurrentDesktopId = desktops.FirstOrDefault()?.Id ?? Guid.NewGuid();
			}

			internal static FakeSource With(params FakeDesktop[] desktops) => WithCapabilities(true, true, true, true, desktops);
			internal static FakeSource WithCapabilities(bool name, bool wallpaper, params FakeDesktop[] desktops) => WithCapabilities(true, true, name, wallpaper, desktops);
			internal static FakeSource WithCapabilities(bool enumerate, bool current, bool name, bool wallpaper, params FakeDesktop[] desktops) => new FakeSource(new VirtualDesktopSnapshotCapabilities("synthetic", enumerate, current, name, wallpaper), desktops);

			public VirtualDesktopSnapshotCapabilities Capabilities { get; }
			internal FakeCollection Collection { get; }
			internal Exception CollectionError { get; set; }
			internal Exception CurrentError { get; set; }
			internal Guid CurrentDesktopId { get; set; }
			internal bool Alive { get; set; } = true;
			internal int CollectionCalls { get; private set; }
			internal int CurrentCalls { get; private set; }

			public IVirtualDesktopSnapshotCollection GetDesktopCollection()
			{
				this.CollectionCalls++;
				this.EnsureAlive();
				if (this.CollectionError != null) throw this.CollectionError;
				return this.Collection;
			}

			public Guid GetCurrentDesktopId()
			{
				this.CurrentCalls++;
				this.EnsureAlive();
				if (this.CurrentError != null) throw this.CurrentError;
				return this.CurrentDesktopId;
			}

			private void EnsureAlive()
			{
				if (!this.Alive) throw new ObjectDisposedException("synthetic source");
			}
		}

		private sealed class FakeCollection : IVirtualDesktopSnapshotCollection
		{
			private readonly List<FakeDesktop> _desktops;
			internal FakeCollection(IEnumerable<FakeDesktop> desktops) => this._desktops = new List<FakeDesktop>(desktops);
			internal Exception CountError { get; set; }
			internal int? CountOverride { get; set; }
			internal Dictionary<int, Exception> GetErrors { get; } = new Dictionary<int, Exception>();
			internal HashSet<int> NullIndexes { get; } = new HashSet<int>();

			public int GetCount()
			{
				if (this.CountError != null) throw this.CountError;
				return this.CountOverride ?? this._desktops.Count;
			}

			public IVirtualDesktopSnapshotValueReader GetDesktop(int index)
			{
				if (this.GetErrors.TryGetValue(index, out var error)) throw error;
				if (this.NullIndexes.Contains(index)) return null;
				return this._desktops[index];
			}
		}

		private sealed class FakeDesktop : IVirtualDesktopSnapshotValueReader
		{
			internal static FakeDesktop Success(string name, string wallpaper) => new FakeDesktop { Id = Guid.NewGuid(), Name = name, Wallpaper = wallpaper };
			internal Guid Id { get; set; }
			internal string Name { get; set; }
			internal string Wallpaper { get; set; }
			internal Exception IdError { get; set; }
			internal Exception NameError { get; set; }
			internal Exception WallpaperError { get; set; }
			internal int IdCalls { get; private set; }
			internal int NameCalls { get; private set; }
			internal int WallpaperCalls { get; private set; }
			internal bool Alive { get; set; } = true;

			public Guid GetId()
			{
				this.EnsureAlive();
				this.IdCalls++;
				if (this.IdError != null) throw this.IdError;
				return this.Id;
			}

			public string GetName()
			{
				this.EnsureAlive();
				this.NameCalls++;
				if (this.NameError != null) throw this.NameError;
				return this.Name;
			}

			public string GetWallpaperPath()
			{
				this.EnsureAlive();
				this.WallpaperCalls++;
				if (this.WallpaperError != null) throw this.WallpaperError;
				return this.Wallpaper;
			}

			private void EnsureAlive()
			{
				if (!this.Alive) throw new ObjectDisposedException("synthetic desktop");
			}
		}
	}
}
