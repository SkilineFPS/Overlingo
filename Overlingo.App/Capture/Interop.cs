using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Overlingo.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace Overlingo.App.Capture;

internal static class D3DInterop
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    static IDirect3DDevice? _device;
    public static IDirect3DDevice Device => _device ??= Create();

    static IDirect3DDevice Create()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx));
        try
        {
            var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(dev, in iidDxgiDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(ctx); Marshal.Release(dev); }
    }
}

internal static class CaptureItemFactory
{
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

    public static GraphicsCaptureItem ForWindow(IntPtr hwnd) => Create(hwnd, 3);
    public static GraphicsCaptureItem ForMonitor(IntPtr hmonitor) => Create(hmonitor, 4);

    static unsafe GraphicsCaptureItem Create(IntPtr handle, int slot)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hs));
        try
        {
            var iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iidInterop, out var factory));
            try
            {
                var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                IntPtr result = IntPtr.Zero;
                var vtbl = *(IntPtr**)factory;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];
                Marshal.ThrowExceptionForHR(fn(factory, handle, &iidItem, &result));
                return GraphicsCaptureItem.FromAbi(result);
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hs); }
    }
}

internal static class BitmapUtil
{
    public static Frame ToFrame(SoftwareBitmap bmp)
    {
        SoftwareBitmap? conv = null; var src = bmp;
        if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        { conv = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied); src = conv; }
        try
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            uint size = (uint)(w * h * 4);
            var buf = new Windows.Storage.Streams.Buffer(size) { Length = size };
            src.CopyToBuffer(buf);
            var arr = new byte[size];
            DataReader.FromBuffer(buf).ReadBytes(arr);
            // Alfa kanál zdrojů (Elgato) může být 0 – pro OCR a náhled ho vždy nastavíme na 255.
            for (int i = 3; i < arr.Length; i += 4) arr[i] = 255;
            return new Frame(w, h, arr);
        }
        finally { conv?.Dispose(); }
    }

    public static SoftwareBitmap ToSoftwareBitmap(Frame f) =>
        SoftwareBitmap.CreateCopyFromBuffer(f.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, f.Width, f.Height, BitmapAlphaMode.Premultiplied);

    public static async Task<Frame> LoadFileAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var dec = await BitmapDecoder.CreateAsync(stream);
        using var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return ToFrame(bmp);
    }
}
