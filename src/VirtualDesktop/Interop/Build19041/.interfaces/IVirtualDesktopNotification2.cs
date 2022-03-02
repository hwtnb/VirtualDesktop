using System;
using System.Runtime.InteropServices;
using WindowsDesktop.Interop.Build10240;

namespace WindowsDesktop.Interop.Build19041
{
    [ComImport]
    [Guid("00000000-0000-0000-0000-000000000000") /* replace at runtime */]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktopNotification2
    {
        void VirtualDesktopCreated(IVirtualDesktop pDesktop);

        void VirtualDesktopDestroyBegin(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback);

        void VirtualDesktopDestroyFailed(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback);

        void VirtualDesktopDestroyed(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback);

        void ViewVirtualDesktopChanged(IApplicationView pView);

        void CurrentVirtualDesktopChanged(IVirtualDesktop pDesktopOld, IVirtualDesktop pDesktopNew);

        void VirtualDesktopRenamed(IVirtualDesktop pDesktop, HString chName);
    }

    internal class VirtualDesktopNotification2 : VirtualDesktopNotificationService.EventListenerBase, IVirtualDesktopNotification, IVirtualDesktopNotification2
    {
        public void VirtualDesktopCreated(IVirtualDesktop pDesktop)
        {
            this.CreatedCore(pDesktop);
        }

        public void VirtualDesktopDestroyBegin(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback)
        {
            this.DestroyBeginCore(pDesktopDestroyed, pDesktopFallback);
        }

        public void VirtualDesktopDestroyFailed(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback)
        {
            this.DestroyFailedCore(pDesktopDestroyed, pDesktopFallback);
        }

        public void VirtualDesktopDestroyed(IVirtualDesktop pDesktopDestroyed, IVirtualDesktop pDesktopFallback)
        {
            this.DestroyedCore(pDesktopDestroyed, pDesktopFallback);
        }

        public void ViewVirtualDesktopChanged(IApplicationView pView)
        {
            this.ViewChangedCore(pView);
        }

        public void CurrentVirtualDesktopChanged(IVirtualDesktop pDesktopOld, IVirtualDesktop pDesktopNew)
        {
            this.CurrentChangedCore(pDesktopOld, pDesktopNew);
        }

        public void VirtualDesktopRenamed(IVirtualDesktop pDesktop, HString chName)
        {
            this.RenamedCore(pDesktop, chName);
        }
    }
}
