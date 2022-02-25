using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using WinRT;
#endif

namespace WindowsDesktop.Interop
{
	// ## Note
	// .NET 5 has removed WinRT support, so HString cannot marshal to System.String.
	// Since marshalling with UnmanagedType.HString fails, use IntPtr to get the string via C#/WinRT MarshalString.
	// 
	// see also: https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md
	// original: https://github.com/Grabacr07/VirtualDesktop/blob/master/src/VirtualDesktop/Interop/HString.cs

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
