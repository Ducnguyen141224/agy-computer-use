using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private object CaptureDisplay(JsonElement args, string displayId)
    {
        var display = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == displayId) ?? throw new DesktopError("DISPLAY_NOT_FOUND", "Choose display_id from list_displays.");
        var full = display.Bounds;
        var visible = full;
        if (args.TryGetProperty("region", out var region))
        {
            visible = new Rectangle(full.Left + region.Number("x", 0, 0, 32768), full.Top + region.Number("y", 0, 0, 32768), region.Number("width", 0, 1, 8192), region.Number("height", 0, 1, 8192));
            if (!full.Contains(visible)) throw new DesktopError("INVALID_ARGUMENT", "Display region must be inside the selected display.");
        }
        if ((long)visible.Width * visible.Height > 16000000) throw new DesktopError("CAPTURE_TOO_LARGE", "Choose a smaller display region.");
        _ = ListSurfaces(JsonSerializer.SerializeToElement(new { }));
        _ = ListWindows(JsonSerializer.SerializeToElement(new { limit = 60 }));
        var baseline = new Dictionary<string, Native.Rect>();
        foreach (var entry in windows.Values)
            if (Native.IsWindow(entry.Handle) && Native.GetWindowRect(entry.Handle, out var rect)) baseline[entry.Id] = rect;
        using var bitmap = new Bitmap(visible.Width, visible.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(visible.Location, System.Drawing.Point.Empty, visible.Size);
        var maximum = args.Number("max_dimension", 1000, 200, 1600);
        var scale = Math.Min(1.0, maximum / (double)Math.Max(visible.Width, visible.Height));
        var width = Math.Max(1, (int)Math.Round(visible.Width * scale));
        var height = Math.Max(1, (int)Math.Round(visible.Height * scale));
        using var resized = new Bitmap(bitmap, width, height);
        using var memory = new MemoryStream(); resized.Save(memory, ImageFormat.Png);
        if (Screen.AllScreens.FirstOrDefault(item => item.DeviceName == displayId)?.Bounds != full) throw new DesktopError("DISPLAY_CHANGED", "Display geometry changed during capture.");
        var id = $"c-{epoch}-{++sequence}";
        var rectangle = new Native.Rect { Left = visible.Left, Top = visible.Top, Right = visible.Right, Bottom = visible.Bottom };
        captures[id] = new(null, rectangle, width, height, DateTime.UtcNow, displayId, baseline);
        windowGeometry[id] = new Native.Rect { Left = full.Left, Top = full.Top, Right = full.Right, Bottom = full.Bottom };
        return new { capture_id = id, display_id = displayId, mode = "display", width, height, source_region = new { x = visible.Left - full.Left, y = visible.Top - full.Top, width = visible.Width, height = visible.Height }, expires_in_ms = 10000, image = Convert.ToBase64String(memory.ToArray()), note = "Visible display pixels, including overlays. Point input still requires an explicitly selected foreground window, unchanged geometry and an unobscured point. Capture is single-use." };
    }
}
