using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDesktop.Interop.Proxy;

namespace WindowsDesktop.Interop.Build19041;

internal class VirtualDesktop : ComWrapperBase<IVirtualDesktop>, IVirtualDesktop
{
    private Guid? _id;

    public VirtualDesktop(ComInterfaceAssembly assembly, object comObject)
        : base(assembly, comObject, version: 2)
    {
    }

    public bool IsViewVisible(IntPtr hWnd)
        => this.InvokeMethod<bool>(Args(hWnd));

    public Guid GetID()
        => this._id ?? (Guid)(this._id = this.InvokeMethod<Guid>());

    public string GetName()
        => this.InvokeMethod<HString>();

    public string GetWallpaperPath()
        => "";
}
