using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Spotifly.WallpaperHost;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11DeviceGuid = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid ID3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, [In] ref Guid iid, out nint result);
        void CreateForMonitor(nint monitor, [In] ref Guid iid, out nint result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static GraphicsCaptureItem CreateItemForWindow(nint window)
    {
        IGraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        interop.CreateForWindow(window, GraphicsCaptureItemGuid, out nint raw);
        try
        {
            return GraphicsCaptureItem.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    public static (IDirect3DDevice Runtime, SharpDX.Direct3D11.Device Native) CreateDevice()
    {
        var native = new SharpDX.Direct3D11.Device(
            SharpDX.Direct3D.DriverType.Hardware,
            SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
        using var dxgi = native.QueryInterface<SharpDX.DXGI.Device3>();
        uint result = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint raw);
        if (result != 0)
        {
            native.Dispose();
            Marshal.ThrowExceptionForHR(unchecked((int)result));
        }

        try
        {
            return (MarshalInterface<IDirect3DDevice>.FromAbi(raw), native);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    public static SharpDX.Direct3D11.Texture2D GetTexture(IDirect3DSurface surface)
    {
        IDirect3DDxgiInterfaceAccess access = surface.As<IDirect3DDxgiInterfaceAccess>();
        nint pointer = access.GetInterface(ID3D11Texture2DGuid);
        return new SharpDX.Direct3D11.Texture2D(pointer);
    }

    public static SharpDX.Direct3D11.Device GetNativeDevice(IDirect3DDevice device)
    {
        IDirect3DDxgiInterfaceAccess access = device.As<IDirect3DDxgiInterfaceAccess>();
        nint pointer = access.GetInterface(ID3D11DeviceGuid);
        return new SharpDX.Direct3D11.Device(pointer);
    }
}
