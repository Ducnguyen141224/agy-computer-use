using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace AgyComputerUse;

// A small premultiplied-alpha surface. The static display border is never animated.
internal sealed class PointerPresenter : Form
{
    private const int Side = 112, Inset = 40;
    private readonly FrameClock clock;
    private readonly IntPtr dc, dib, previous;
    private readonly Bitmap surface;
    private readonly Graphics graphics;
    private readonly Bitmap arrow;
    private readonly Queue<double> intervals = new();
    private PointF origin, target, initialVelocity;
    private double moved, duration, pulse = -1000, lastFrame;
    private long frames;
    private bool hidden = true;
    private bool resourcesReleased;
    internal static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
    internal PointerPresenter()
    {
        Text = "AGY animated pointer"; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual; Size = new(Side, Side); TopMost = true;
        dc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BitmapInfo { Header = new() { Size = 40, Width = Side, Height = -Side, Planes = 1, BitCount = 32, SizeImage = Side * Side * 4 } };
        dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dc == IntPtr.Zero || dib == IntPtr.Zero) { if (dib != IntPtr.Zero) DeleteObject(dib); if (dc != IntPtr.Zero) DeleteDC(dc); throw new DesktopError("PRESENTATION_UNAVAILABLE", "Could not allocate pointer surface."); }
        previous = SelectObject(dc, dib);
        surface = new Bitmap(Side, Side, Side * 4, PixelFormat.Format32bppPArgb, bits);
        graphics = Graphics.FromImage(surface); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        arrow = new Bitmap(32, 38, PixelFormat.Format32bppPArgb);
        using (var paint = Graphics.FromImage(arrow))
        {
            paint.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = new Pen(Color.White, 1.5f) { LineJoin = LineJoin.Round };
            using var ink = new SolidBrush(Color.FromArgb(17, 17, 17));
            PointF[] points = [new(2, 2), new(2, 26), new(8, 20), new(13, 31), new(18, 28), new(13, 18), new(25, 18)];
            paint.FillPolygon(ink, points); paint.DrawPolygon(outline, points);
        }
        Native.GetPhysicalCursorPos(out var point); origin = target = new(point.X, point.Y);
        clock = new FrameClock(Tick);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } }
    protected override void WndProc(ref Message message) { if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; } if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } base.WndProc(ref message); }
    internal void MovePointer(Point point, bool clicked)
    {
        if (hidden) Wake();
        if (hidden) return;
        var now = Now;
        if (Math.Abs(point.X - target.X) > .25 || Math.Abs(point.Y - target.Y) > .25)
        {
            var current = Sample(now); origin = current.Position; target = point;
            var dx = target.X - origin.X; var dy = target.Y - origin.Y; var distance = Math.Sqrt(dx * dx + dy * dy);
            duration = Math.Clamp(55 + distance * .09, 65, 155);
            var velocity = current.Velocity; var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
            var maximum = distance * 1.5 / duration;
            initialVelocity = velocity.X * dx + velocity.Y * dy <= 0 || speed == 0 ? new() : new((float)(velocity.X * Math.Min(1, maximum / speed)), (float)(velocity.Y * Math.Min(1, maximum / speed)));
            moved = now;
        }
        if (clicked) pulse = now;
        clock.Wake();
    }
    private (PointF Position, PointF Velocity) Sample(double now)
    {
        var t = duration <= 0 ? 1 : Math.Clamp((now - moved) / duration, 0, 1);
        if (t >= 1) return (target, new());
        var t2 = t * t; var t3 = t2 * t;
        var a = 2 * t3 - 3 * t2 + 1; var b = t3 - 2 * t2 + t; var c = -2 * t3 + 3 * t2;
        var da = (6 * t2 - 6 * t) / duration; var db = 3 * t2 - 4 * t + 1; var dc = (-6 * t2 + 6 * t) / duration;
        return (new((float)(a * origin.X + b * initialVelocity.X * duration + c * target.X), (float)(a * origin.Y + b * initialVelocity.Y * duration + c * target.Y)), new((float)(da * origin.X + db * initialVelocity.X + dc * target.X), (float)(da * origin.Y + db * initialVelocity.Y + dc * target.Y)));
    }
    internal void Wake() { if (CursorOverlay.HasEnabledProfile) { Rest(); return; } if (!hidden) return; Native.GetPhysicalCursorPos(out var point); origin = target = new(point.X, point.Y); duration = 0; hidden = false; Render(Now); if (!Visible) Show(); }
    internal void Rest() { hidden = true; clock.Rest(); if (Visible) Hide(); }
    private void Tick()
    {
        if (!ControlPresentation.Active || CursorOverlay.HasEnabledProfile || hidden) { Rest(); return; }
        var now = Now;
        try { Render(now); } catch (DesktopError) { ControlPresentation.Stop(); return; }
        if (now - moved >= duration && now - pulse >= 440) clock.Rest();
    }
    private void Render(double now)
    {
        var p = Sample(now).Position;
        var left = (int)Math.Floor(p.X) - Inset; var top = (int)Math.Floor(p.Y) - Inset;
        graphics.Clear(Color.Transparent);
        var fractionX = p.X - MathF.Floor(p.X); var fractionY = p.Y - MathF.Floor(p.Y);
        graphics.DrawImage(arrow, Inset - 2 + fractionX, Inset - 2 + fractionY, arrow.Width, arrow.Height);
        if (now - pulse is >= 0 and < 440)
        {
            var t = (now - pulse) / 440; var radius = (float)(10 + 27 * t);
            using var ripple = new Pen(Color.FromArgb((int)(160 * (1 - t)), 59, 130, 246), 2);
            graphics.DrawEllipse(ripple, Inset - radius, Inset - radius, radius * 2, radius * 2);
        }
        graphics.Flush(FlushIntention.Sync);
        var position = new Native.Point(left, top); var size = new Native.Point(Side, Side); var source = new Native.Point();
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, dc, ref source, 0, ref blend, 2)) throw new DesktopError("PRESENTATION_UNAVAILABLE", "Windows could not update the pointer surface.");
        if (lastFrame > 0 && now - lastFrame < 100) { intervals.Enqueue(now - lastFrame); if (intervals.Count > 240) intervals.Dequeue(); }
        lastFrame = now; frames++;
    }
    internal object Metrics()
    {
        var sorted = intervals.Order().ToArray();
        return new { renderer = "small premultiplied-alpha layered window", thread = "dedicated presentation STA", pointer_fill = "#111111", pointer_outline = "#FFFFFF", upload_width = Side, upload_height = Side, uploaded_frames = frames, motion_active = Now - moved < duration, target_hz = 120, high_resolution_clock = clock.HighResolution, sample_count = sorted.Length, median_submit_interval_ms = sorted.Length == 0 ? (double?)null : Math.Round(sorted[sorted.Length / 2], 2), p95_submit_interval_ms = sorted.Length == 0 ? (double?)null : Math.Round(sorted[(int)((sorted.Length - 1) * .95)], 2), note = "Submission timing only; not measured display FPS. RAM counters." };
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesReleased) { resourcesReleased = true; clock.Dispose(); graphics.Dispose(); surface.Dispose(); arrow.Dispose(); SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
        base.Dispose(disposing);
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

    private sealed class FrameClock : IDisposable
    {
        private readonly ManualResetEvent active = new(false), stop = new(false);
        private readonly EventWaitHandle timer = new(false, EventResetMode.AutoReset);
        private readonly Thread thread;
        private int queued;
        private volatile bool closed;
        internal bool HighResolution { get; }
        internal FrameClock(Action render)
        {
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003); HighResolution = handle != IntPtr.Zero;
            if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
            if (handle == IntPtr.Zero) throw new DesktopError("PRESENTATION_UNAVAILABLE", "Frame pacing timer is unavailable.");
            var oldHandle = timer.SafeWaitHandle; timer.SafeWaitHandle = new SafeWaitHandle(handle, true); oldHandle.Dispose();
            thread = new Thread(() =>
            {
                while (WaitHandle.WaitAny([stop, active]) != 0)
                {
                    if (Interlocked.Exchange(ref queued, 1) == 0) PresentationDispatcher.Post(() => { Interlocked.Exchange(ref queued, 0); if (!closed) render(); });
                    long due = -83333; // 120 Hz, one-shot: no process-wide timer-resolution change.
                    if (!SetWaitableTimer(timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) break;
                    if (WaitHandle.WaitAny([stop, timer]) == 0) break;
                }
            }) { IsBackground = true, Name = "AGY pointer frame clock" };
            thread.Start();
        }
        internal void Wake() => active.Set();
        internal void Rest() => active.Reset();
        public void Dispose() { if (closed) return; closed = true; stop.Set(); thread.Join(500); timer.Dispose(); active.Dispose(); stop.Dispose(); }
        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerEx(IntPtr security, string? name, uint flags, uint access);
        [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
    }
}
