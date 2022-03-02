using System;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop.Build10240;

namespace WindowsDesktop.Interop.Build19041
{
    [ComImport]
    [Guid("00000000-0000-0000-0000-000000000000") /* replace at runtime */]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktopManagerInternal2
    {
        int GetCount();

        void MoveViewToDesktop(IApplicationView pView, IVirtualDesktop desktop);

        bool CanViewMoveDesktops(IApplicationView pView);

        IVirtualDesktop GetCurrentDesktop();

        IObjectArray GetDesktops();

        IVirtualDesktop GetAdjacentDesktop(IVirtualDesktop pDesktopReference, int uDirection);

        void SwitchDesktop(IVirtualDesktop desktop);

        IVirtualDesktop CreateDesktop();

        void RemoveDesktop(IVirtualDesktop pRemove, IVirtualDesktop pFallbackDesktop);

        IVirtualDesktop FindDesktop(in Guid desktopId);

        void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray o1, out IObjectArray o2);

        void SetDesktopName(IVirtualDesktop desktop, HString name);
    }
}
