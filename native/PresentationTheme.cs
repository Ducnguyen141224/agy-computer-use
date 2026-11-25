using System.Drawing;
using System.Drawing.Drawing2D;

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
    internal static void DrawFrame(Graphics g, Rectangle bounds)
    {
        // The glow fades into the actual desktop through per-pixel alpha.
        for (var inset = 35; inset >= 2; inset--)
        {
            var alpha = (int)(108 * Math.Exp(-(inset - 2) / 10d));
            using var pen = new Pen(Color.FromArgb(alpha, Aura), 1.8f);
            var rect = new RectangleF(bounds.Left + inset, bounds.Top + inset, bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
            using var shape = new GraphicsPath(); shape.AddRoundedRectangle(rect, new SizeF(Math.Max(3, 14 - inset / 3f), Math.Max(3, 14 - inset / 3f))); g.DrawPath(pen, shape);
        }
        using var edge = new Pen(Edge, 2.2f);
        using var outline = new GraphicsPath(); outline.AddRoundedRectangle(new RectangleF(bounds.Left + 2, bounds.Top + 2, bounds.Width - 5, bounds.Height - 5), new SizeF(11, 11)); g.DrawPath(edge, outline);
    }
    internal static void DrawLabel(Graphics g, Rectangle primary)
    {
        var label = new Rectangle(primary.Left + (primary.Width - 360) / 2, primary.Top + 30, 360, 44);
        for (var spread = 10; spread >= 1; spread--)
        {
            using var shadow = new Pen(Color.FromArgb((int)(24 * Math.Exp(-spread / 4d)), Aura), 2);
            using var glow = new GraphicsPath(); glow.AddRoundedRectangle(Rectangle.Inflate(label, spread, spread), new Size(10 + spread, 10 + spread)); g.DrawPath(shadow, glow);
        }
        using var path = new GraphicsPath(); path.AddRoundedRectangle(label, new Size(9, 9));
        using var fill = new LinearGradientBrush(label, LabelTop, LabelBottom, LinearGradientMode.Vertical); g.FillPath(fill, path);
        using var rim = new Pen(Color.FromArgb(160, 120, 215, 255), 1); g.DrawPath(rim, path);
        using var font = new Font("Segoe UI", 10f, FontStyle.Regular);
        using var text = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("AGY đang điều khiển  ·  ESC để dừng", font, text, label, format);
    }
}
