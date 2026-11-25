using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace AgyComputerUse;

internal static class WindowCapture
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CaptureInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface SurfaceAccess { [PreserveSig] int GetInterface(in Guid iid, out IntPtr result); }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, in Guid iid, out IntPtr result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Native.Rect rect, int size);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private static readonly object deviceGate = new();
    private static ID3D11Device? sharedDevice;
    private static ID3D11DeviceContext? sharedContext;
    private static IDirect3DDevice? sharedWinDevice;

    public static bool Supported => GraphicsCaptureSession.IsSupported();
    internal static Native.Rect Bounds(IntPtr window)
    {
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0) Native.GetWindowRect(window, out rect);
        return rect;
    }

    internal static void DisposeShared()
    {
        lock (deviceGate)
        {
            (sharedWinDevice as IDisposable)?.Dispose(); sharedWinDevice = null;
            sharedContext?.Dispose(); sharedContext = null;
            sharedDevice?.Dispose(); sharedDevice = null;
        }
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context, IDirect3DDevice WinDevice) EnsureSharedDevice()
    {
        if (sharedDevice != null && sharedContext != null && sharedWinDevice != null)
        {
            if (sharedDevice.DeviceRemovedReason.Success) return (sharedDevice, sharedContext, sharedWinDevice);
            DisposeShared();
        }
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context);
        if (result.Failure) result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out device, out context);
        result.CheckError();
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
            var winDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
            Marshal.Release(pointer);
            sharedDevice = device;
            sharedContext = context;
            sharedWinDevice = winDevice;
            return (device, context, winDevice);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    private static GraphicsCaptureItem Item(IntPtr window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            var factoryIid = typeof(CaptureInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, factoryIid, out factory));
            var interop = (CaptureInterop)Marshal.GetObjectForIUnknown(factory);
            try
            {
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, itemIid, out item));
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
            }
            finally { Marshal.ReleaseComObject(interop); }
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hstring);
        }
    }

    internal static Bitmap CapturePrintWindow(IntPtr window)
    {
        var rect = Bounds(window);
        if (rect.Width <= 0 || rect.Height <= 0) throw new DesktopError("CAPTURE_UNAVAILABLE", "Window has no capture area.");
        if ((long)rect.Width * rect.Height > 16000000) throw new DesktopError("CAPTURE_TOO_LARGE", "Window exceeds the capture pixel budget.");
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(window, hdc, 2) && !PrintWindow(window, hdc, 0))
                {
                    bitmap.Dispose();
                    throw new DesktopError("CAPTURE_UNAVAILABLE", "PrintWindow fallback failed.");
                }
            }
            finally { g.ReleaseHdc(hdc); }
        }
        return bitmap;
    }

    public static Bitmap Capture(IntPtr window, int timeoutMs = 2200)
    {
        if (!Supported) return CapturePrintWindow(window);
        lock (deviceGate)
        {
            try { return CaptureWithDevice(window, timeoutMs); }
            catch (SharpGen.Runtime.SharpGenException)
            {
                DisposeShared();
                return CaptureWithDevice(window, timeoutMs);
            }
        }
    }

    private static Bitmap CaptureWithDevice(IntPtr window, int timeoutMs)
    {
        var (device, context, winDevice) = EnsureSharedDevice();
        var item = Item(window);
        if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new DesktopError("CAPTURE_UNAVAILABLE", "Window capture reported empty dimensions.");
        if ((long)item.Size.Width * item.Size.Height > 16000000) throw new DesktopError("CAPTURE_TOO_LARGE", "Window exceeds the capture pixel budget.");
        using var arrived = new ManualResetEventSlim(false);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        pool.FrameArrived += (_, _) => arrived.Set();
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
        var timer = Stopwatch.StartNew();
        var empty = false;
        while (true)
        {
            ControlPresentation.Check();
            using var frame = pool.TryGetNextFrame();
            if (frame != null)
            {
                try { return ReadFrame(frame, device, context); }
                catch (DesktopError error) when (error.Code == "CAPTURE_EMPTY") { empty = true; }
            }
            var remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
            if (remaining <= 0) break;
            arrived.Reset();
            arrived.Wait(Math.Min(remaining, 35));
        }
        throw new DesktopError(empty ? "CAPTURE_EMPTY" : "CAPTURE_TIMEOUT", empty ? "Windows returned only transparent window frames. No point-input token was created." : "No capture frame arrived before timeout. No screen fallback was used.");
    }

    private static Bitmap ReadFrame(Direct3D11CaptureFrame frame, ID3D11Device device, ID3D11DeviceContext context)
    {
        var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        IntPtr accessPointer = IntPtr.Zero, texturePointer = IntPtr.Zero;
        try
        {
            var accessIid = typeof(SurfaceAccess).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessIid, out accessPointer));
            var access = (SurfaceAccess)Marshal.GetObjectForIUnknown(accessPointer);
            try
            {
                var textureIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                Marshal.ThrowExceptionForHR(access.GetInterface(textureIid, out texturePointer));
            }
            finally { Marshal.ReleaseComObject(access); }
            using var texture = new ID3D11Texture2D(texturePointer);
            texturePointer = IntPtr.Zero;
            var description = texture.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(description);
            context.CopyResource(staging, texture);
            context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var width = Math.Min(frame.ContentSize.Width, (int)description.Width);
                var height = Math.Min(frame.ContentSize.Height, (int)description.Height);
                if (width <= 0 || height <= 0) throw new DesktopError("CAPTURE_EMPTY", "Window frame had empty dimensions.");
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var opaque = false;
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[width * 4];
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(mapped.DataPointer + (int)mapped.RowPitch * y, row, 0, row.Length);
                        if (!opaque) for (var channel = 3; channel < row.Length; channel += 4) if (row[channel] != 0) { opaque = true; break; }
                        Marshal.Copy(row, 0, bits.Scan0 + bits.Stride * y, row.Length);
                    }
                }
                finally { bitmap.UnlockBits(bits); }
                if (!opaque) { bitmap.Dispose(); throw new DesktopError("CAPTURE_EMPTY", "Window frame contains no visible pixels."); }
                return bitmap;
            }
            finally { context.Unmap(staging, 0); }
        }
        finally
        {
            if (texturePointer != IntPtr.Zero) Marshal.Release(texturePointer);
            if (accessPointer != IntPtr.Zero) Marshal.Release(accessPointer);
            Marshal.Release(surface);
        }
    }
}
