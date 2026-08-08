using System;
using WindowsDesktop.Interop;
using Xunit;

namespace WindowsDesktop.Tests
{
	public sealed class HStringTests
	{
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("Desktop name")]
		public void OwnedAbiConvertsToManagedValue(string value)
		{
			var hstring = HString.FromManagedOwned(value);
			var actual = hstring.ToManagedAndDispose();
			if (value == null) Assert.True(string.IsNullOrEmpty(actual));
			else Assert.Equal(value, actual);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("Desktop name")]
		public void InputMarshalerKeepsBorrowedAbiAliveUntilDisposed(string value)
		{
			var marshaler = HString.CreateMarshaler(value);
			var actual = marshaler.Value.ToManaged();
			if (value == null) Assert.True(string.IsNullOrEmpty(actual));
			else Assert.Equal(value, actual);
			marshaler.Dispose();
			Assert.Throws<ObjectDisposedException>(() => marshaler.Value);
			marshaler.Dispose();
		}

		[Fact]
		public void UnicodeValueRoundTripsThroughOwnedAndBorrowedAbi()
		{
			const string value = "\u4eee\u60f3\u30c7\u30b9\u30af\u30c8\u30c3\u30d7";
			var owned = HString.FromManagedOwned(value);
			Assert.Equal(value, owned.ToManagedAndDispose());

			using (var marshaler = HString.CreateMarshaler(value))
			{
				Assert.Equal(value, marshaler.Value.ToManaged());
			}
		}
	}
}
