using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static class PresentationTheme
{
    internal const string EdgeHex = "#31A8FF";
    internal const string IceHex = "#F7FCFF";
    internal static readonly Color Edge = Color.FromArgb(49, 168, 255);
    internal static readonly Color Aura = Color.FromArgb(22, 139, 255);
    internal static readonly Color Ink = Color.FromArgb(17, 17, 17);
    internal static readonly Color Ice = Color.FromArgb(247, 252, 255);
    internal static readonly Color LabelTop = Color.FromArgb(8, 113, 189);
    internal static readonly Color LabelBottom = Color.FromArgb(8, 92, 167);

    internal static float MonitorScale(Rectangle monitorBounds)
    {
        try
        {
            var pt = new Native.Point(monitorBounds.Left + Math.Max(1, monitorBounds.Width / 2), monitorBounds.Top + Math.Max(1, monitorBounds.Height / 2));
            var hmon = MonitorFromPoint(pt, 2);
            if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, 0, out var dpiX, out _) == 0 && dpiX >= 96)
                return Math.Clamp(dpiX / 96f, 1f, 3f);
        }
        catch { }
        return 1f;
    }

    internal static void DrawFrame(Graphics g, Rectangle bounds)
    {
        var scale = MonitorScale(bounds);
        var maxInset = Math.Max(20, (int)Math.Round(35 * scale));
        var edgeInset = Math.Max(2, (int)Math.Round(2 * scale));
        // The glow fades into the actual desktop through per-pixel alpha.
        for (var inset = maxInset; inset >= edgeInset; inset--)
        {
            var alpha = (int)(108 * Math.Exp(-(inset - edgeInset) / (10d * scale)));
            using var pen = new Pen(Color.FromArgb(alpha, Aura), 1.8f * scale);
            var rect = new RectangleF(bounds.Left + inset, bounds.Top + inset, bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
            var radius = Math.Max(3f, (14f - inset / (3f * scale)) * scale);
            using var shape = new GraphicsPath(); shape.AddRoundedRectangle(rect, new SizeF(radius, radius)); g.DrawPath(pen, shape);
        }
        using var edge = new Pen(Edge, 2.2f * scale);
        using var outline = new GraphicsPath(); outline.AddRoundedRectangle(new RectangleF(bounds.Left + edgeInset, bounds.Top + edgeInset, bounds.Width - edgeInset * 2 - 1, bounds.Height - edgeInset * 2 - 1), new SizeF(11f * scale, 11f * scale)); g.DrawPath(edge, outline);
    }

    internal static Rectangle DrawLabel(Graphics g, Rectangle primary, string? customText = null, float? scaleOverride = null)
    {
        var scale = scaleOverride ?? MonitorScale(primary);
        var caption = string.IsNullOrWhiteSpace(customText) ? "AGY đang điều khiển  ·  ESC để dừng" : customText;
        var width = (int)Math.Round(Math.Clamp(360 + Math.Max(0, caption.Length - 36) * 7, 360, 560) * scale);
        var height = (int)Math.Round(44 * scale);
        var topOffset = (int)Math.Round(30 * scale);
        var label = new Rectangle(primary.Left + (primary.Width - width) / 2, primary.Top + topOffset, width, height);
        var maxSpread = Math.Max(6, (int)Math.Round(10 * scale));
        for (var spread = maxSpread; spread >= 1; spread--)
        {
            using var shadow = new Pen(Color.FromArgb((int)(24 * Math.Exp(-spread / (4d * scale))), Aura), 2f * scale);
            var corner = (int)Math.Round(10 * scale) + spread;
            using var glow = new GraphicsPath(); glow.AddRoundedRectangle(Rectangle.Inflate(label, spread, spread), new Size(corner, corner)); g.DrawPath(shadow, glow);
        }
        var pillCorner = (int)Math.Round(9 * scale);
        using var path = new GraphicsPath(); path.AddRoundedRectangle(label, new Size(pillCorner, pillCorner));
        using var fill = new LinearGradientBrush(label, LabelTop, LabelBottom, LinearGradientMode.Vertical); g.FillPath(fill, path);
        using var rim = new Pen(Color.FromArgb(160, 120, 215, 255), Math.Max(1f, scale)); g.DrawPath(rim, path);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 13.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(caption, font, text, label, format);
        return label;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Native.Point pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
