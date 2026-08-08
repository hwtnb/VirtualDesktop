using System;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using WinRT;
#endif

namespace WindowsDesktop.Interop
{
	// .NET 5 and later no longer support built-in HSTRING marshalling.
	// Use the C#/WinRT MarshalString ABI on modern .NET while preserving the
	// existing framework marshalling behavior for net48.
#if NET5_0_OR_GREATER
	[StructLayout(LayoutKind.Sequential)]
	public struct HString
	{
		private readonly IntPtr _abi;

		private HString(IntPtr abi) => this._abi = abi;

		internal static HString FromManagedOwned(string value)
			=> new HString(MarshalString.FromManaged(value));

		internal static HStringMarshaler CreateMarshaler(string value)
			=> new HStringMarshaler(value);

		internal string ToManaged()
			=> MarshalString.FromAbi(this._abi);

		public static implicit operator string(HString hStr)
			=> hStr.ToManaged();

		internal string ToManagedAndDispose()
		{
			try { return MarshalString.FromAbi(this._abi); }
			finally { MarshalString.DisposeAbi(this._abi); }
		}

		internal static HString FromBorrowedAbi(IntPtr abi) => new HString(abi);
	}

	internal sealed class HStringMarshaler : IDisposable
	{
		private MarshalString _marshaler;
		private bool _disposed;

		internal HStringMarshaler(string value)
			=> this._marshaler = MarshalString.CreateMarshaler(value);

		internal HString Value
		{
			get
			{
				if (this._disposed) throw new ObjectDisposedException(nameof(HStringMarshaler));
				return HString.FromBorrowedAbi(MarshalString.GetAbi(this._marshaler));
			}
		}

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			var marshaler = this._marshaler;
			this._marshaler = null;
			MarshalString.DisposeMarshaler(marshaler);
		}
	}
#else
	[StructLayout(LayoutKind.Sequential)]
	public struct HString
	{
		[MarshalAs(UnmanagedType.HString)]
		private readonly string _buffer;

		private HString(string str)
		{
			this._buffer = str;
		}

		internal static HString FromManagedOwned(string value) => new HString(value);
		internal static HStringMarshaler CreateMarshaler(string value) => new HStringMarshaler(value);
		internal string ToManaged() => this._buffer;
		internal string ToManagedAndDispose() => this._buffer;
		public static implicit operator string(HString hStr) => hStr.ToManaged();
	}

	internal sealed class HStringMarshaler : IDisposable
	{
		private HString _value;
		private bool _disposed;

		internal HStringMarshaler(string value) => this._value = HString.FromManagedOwned(value);

		internal HString Value
		{
			get
			{
				if (this._disposed) throw new ObjectDisposedException(nameof(HStringMarshaler));
				return this._value;
			}
		}

		public void Dispose() => this._disposed = true;
	}
#endif
}
