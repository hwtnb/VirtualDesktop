using System;
using WindowsDesktop.Interop;

namespace WindowsDesktop
{
	public interface IVirtualDesktopCache
	{
		Func<Guid, object, VirtualDesktop> Factory { get; set; }

		VirtualDesktop GetOrCreate(object comObject);

		void Clear();
	}

	internal static class VirtualDesktopCache
	{
		private static IVirtualDesktopCache _cache;

		public static void Initialize(ComInterfaceAssembly assembly)
		{
			if (_cache == null)
			{
				var type2 = assembly.GetType("VirtualDesktopCacheImpl2");
				if (type2 != null)
				{
					_cache = (IVirtualDesktopCache)Activator.CreateInstance(type2);
					_cache.Factory = (id, comObject) => new VirtualDesktop(assembly, id, comObject);
				}
				else
				{
					var type = assembly.GetType("VirtualDesktopCacheImpl");
					_cache = (IVirtualDesktopCache)Activator.CreateInstance(type);
					_cache.Factory = (id, comObject) => new VirtualDesktop(assembly, id, comObject);
				}
			}
			else
			{
				_cache.Clear();
			}

			ImmersiveShellHandle = NativeMethods.FindWindow(NativeMethods.ImmersiveShellClassName, null);
			TaskbarHandle = NativeMethods.FindWindow(NativeMethods.TaskbarClassName, null);
		}

		public static VirtualDesktop GetOrCreate(object comObject)
			=> _cache.GetOrCreate(comObject);

		public static IntPtr ImmersiveShellHandle { get; private set; } = IntPtr.Zero;

		public static IntPtr TaskbarHandle { get; private set; } = IntPtr.Zero;
	}
}
