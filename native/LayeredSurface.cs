using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgyComputerUse;

// Reusable premultiplied-alpha DIB; no color key, screen capture, or disk asset.
internal sealed class LayeredSurface : IDisposable
{
    private readonly IntPtr dc, dib, previous;
    private readonly Bitmap bitmap;
    private bool disposed;
    internal Graphics Graphics { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal LayeredSurface(int width, int height)
    {
        Width = width; Height = height;
        dc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BitmapInfo { Header = new() { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4)) } };
        dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dc == IntPtr.Zero || dib == IntPtr.Zero) { if (dib != IntPtr.Zero) DeleteObject(dib); if (dc != IntPtr.Zero) DeleteDC(dc); throw new DesktopError("PRESENTATION_UNAVAILABLE", "Could not allocate presentation surface."); }
        previous = SelectObject(dc, dib);
        bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        Graphics = Graphics.FromImage(bitmap);
    }
    internal void Present(IntPtr window, int left, int top)
    {
        Graphics.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        var position = new Native.Point(left, top); var size = new Native.Point(Width, Height); var source = new Native.Point();
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, dc, ref source, 0, ref blend, 2)) throw new DesktopError("PRESENTATION_UNAVAILABLE", "Windows could not update the presentation surface.");
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Graphics.Dispose(); bitmap.Dispose(); SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { internal byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage; internal int XPels, YPels; internal uint Colors, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal Header Header; internal uint Color; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref Native.Point point, ref Native.Point size, IntPtr source, ref Native.Point sourcePoint, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
