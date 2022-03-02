using System;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop.Build10240;

namespace WindowsDesktop.Interop.Build19041
{
    [ComImport]
    [Guid("00000000-0000-0000-0000-000000000000") /* replace at runtime */]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktop2
    {
        bool IsViewVisible(IApplicationView view);

        Guid GetID();

        HString GetName();
    }
}
