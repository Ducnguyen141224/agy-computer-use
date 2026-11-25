using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AgyComputerUse;

// Compact per-pixel-alpha target highlight box (no full-screen repaint, no magenta key).
internal sealed class HighlightPresenter : Form
{
    internal const int Pad = 12;
    private const double FadeMs = 380d;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 16 };
    private LayeredSurface? surface;
    private Rectangle targetBounds;
    private double startedAt;
    private bool hidden = true;
    private bool released;

    private static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    internal HighlightPresenter()
    {
        Text = "AGY target highlight"; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual; TopMost = true;
        clock.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }

    internal static void PaintFrame(Graphics g, int surfaceWidth, int surfaceHeight, float opacity)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new RectangleF(Pad, Pad, surfaceWidth - Pad * 2 - 1, surfaceHeight - Pad * 2 - 1);
        for (var spread = 6; spread >= 1; spread--)
        {
            var a = (int)Math.Clamp(42 * opacity * Math.Exp(-spread / 2.5), 0, 255);
            if (a <= 0) continue;
            using var glow = new Pen(Color.FromArgb(a, PresentationTheme.Aura), 2f);
            using var path = new GraphicsPath();
            var r = RectangleF.Inflate(box, spread, spread);
            path.AddRoundedRectangle(r, new SizeF(6 + spread, 6 + spread));
            g.DrawPath(glow, path);
        }
        using var edge = new Pen(Color.FromArgb((int)Math.Clamp(235 * opacity, 0, 255), PresentationTheme.Edge), 2.2f);
        using var frame = new GraphicsPath();
        frame.AddRoundedRectangle(box, new SizeF(6, 6));
        g.DrawPath(edge, frame);
    }

    internal static Bitmap RenderToBitmap(int width, int height, float opacity = 1f)
    {
        var w = Math.Clamp(width + Pad * 2, Pad * 2 + 4, 2400);
        var h = Math.Clamp(height + Pad * 2, Pad * 2 + 4, 1600);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        PaintFrame(g, w, h, opacity);
        return bmp;
    }

    internal void Flash(Rectangle bounds)
    {
        if (CursorOverlay.HasEnabledProfile || bounds.Width <= 0 || bounds.Height <= 0) { Rest(); return; }
        var clamped = Rectangle.Intersect(bounds, SystemInformation.VirtualScreen);
        if (clamped.Width <= 0 || clamped.Height <= 0) return;
        targetBounds = clamped;
        startedAt = Now;
        var w = Math.Min(clamped.Width + Pad * 2, 2400);
        var h = Math.Min(clamped.Height + Pad * 2, 1600);
        if (surface == null || surface.Width != w || surface.Height != h)
        {
            surface?.Dispose();
            surface = new LayeredSurface(w, h);
        }
        hidden = false;
        Render(1f);
        if (!Visible) Show();
        clock.Start();
    }

    internal void Rest()
    {
        hidden = true;
        clock.Stop();
        if (Visible) Hide();
    }

    private void Tick()
    {
        if (hidden || !ControlPresentation.Active || CursorOverlay.HasEnabledProfile) { Rest(); return; }
        var elapsed = Now - startedAt;
        if (elapsed >= FadeMs) { Rest(); return; }
        var alpha = (float)Math.Pow(1d - elapsed / FadeMs, 1.4);
        try { Render(alpha); } catch (DesktopError) { Rest(); }
    }

    private void Render(float opacity)
    {
        if (surface == null) return;
        PaintFrame(surface.Graphics, surface.Width, surface.Height, opacity);
        surface.Present(Handle, targetBounds.Left - Pad, targetBounds.Top - Pad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true;
            clock.Dispose();
            surface?.Dispose();
            surface = null;
        }
        base.Dispose(disposing);
    }
}
