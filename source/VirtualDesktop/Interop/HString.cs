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

		internal HString(string str)
		{
			this._abi = MarshalString.GetAbi(MarshalString.CreateMarshaler(str));
		}

		public static implicit operator string(HString hStr)
			=> MarshalString.FromAbi(hStr._abi);
	}
#else
	[StructLayout(LayoutKind.Sequential)]
	public struct HString
	{
		[MarshalAs(UnmanagedType.HString)]
		private readonly string _buffer;

		internal HString(string str)
		{
			this._buffer = str;
		}

		public static implicit operator string(HString hStr)
			=> hStr._buffer;
	}
#endif
}
