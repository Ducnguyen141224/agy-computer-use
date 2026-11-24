using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgyComputerUse;

// Click-through activity indicator. Escape latches a stop; it never undoes delivered input.
internal static class ControlPresentation
{
    private static readonly EventWaitHandle Stopped = new(false, EventResetMode.ManualReset,
        "Local\\AgyComputerUse.Stopped." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
    private static readonly HookCallback Callback = Keyboard;
    private static IntPtr hook;
    private static ActivityForm? form;
    private static int owners;
    private static long until;
    private static volatile bool visible;
    internal static IntPtr WindowHandle;
    internal static bool Paused => Stopped.WaitOne(0);
    internal static bool Active => !Paused && (Volatile.Read(ref owners) > 0 || Environment.TickCount64 < Interlocked.Read(ref until));
    internal static IDisposable Begin()
    {
        Check();
        StaDispatcher.Run(() =>
        {
            Check();
            if (hook == IntPtr.Zero)
            {
                hook = SetWindowsHookEx(13, Callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new DesktopError("STOP_HOOK_UNAVAILABLE", "Cannot register Escape stop. No new action was started.");
            }
            form ??= new ActivityForm();
            Interlocked.Increment(ref owners);
            Interlocked.Exchange(ref until, Environment.TickCount64 + 1800);
            CursorOverlay.Suspend(false);
            form.Wake(); visible = true; WindowHandle = form.Handle;
            return true;
        });
        return new Activity();
    }
    private sealed class Activity : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; Interlocked.Decrement(ref owners); Interlocked.Exchange(ref until, Environment.TickCount64 + 1800); }
    }
    internal static void Check()
    {
        if (Paused) throw new DesktopError("USER_STOPPED", "Control was stopped with Escape. Inspect the UI; resume only after a new explicit user instruction.", new { delivery = "unknown", resume = "desktop_system control_resume" });
    }
    internal static void Delay(int milliseconds) { if (Stopped.WaitOne(Math.Max(0, milliseconds))) Check(); }
    internal static object Status() => new { paused = Paused, active = Active, rendered = visible && WindowHandle != IntPtr.Zero, border = "#3B82F6", escape_to_stop = true, idle_hide_ms = 1800, scope = "current interactive user; RAM only", resume_requires = "new explicit user instruction" };
    internal static object Resume() { Stopped.Reset(); return Status(); }
    internal static void Stop()
    {
        var first = !Paused;
        Stopped.Set();
        StaDispatcher.Run(() => { form?.Rest(); visible = false; CursorOverlay.Suspend(true); return true; });
        // The bridge may terminate a blocked helper: release our own input before notifying it.
        ThreadPool.QueueUserWorkItem(_ => { Native.ReleaseOwnedInput(); if (first) Program.ControlStopped(); });
    }
    internal static void Pointer(int x, int y, bool pulse = false)
    {
        if (!Active) return;
        StaDispatcher.Run(() => { form?.MovePointer(new(x, y), pulse); return true; });
    }
    internal static void Close()
    {
        if (form == null && hook == IntPtr.Zero) return;
        StaDispatcher.Run(() => { form?.Close(); form = null; visible = false; WindowHandle = IntPtr.Zero; if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } return true; });
    }
    private static IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyData>(data);
            if (key.Key == 0x1B && key.ExtraInfo != Native.InputTag && (message == new IntPtr(0x100) || message == new IntPtr(0x104)) && Active)
            {
                Stop(); return new IntPtr(1);
            }
            if (key.ExtraInfo == Native.InputTag && Paused && (key.Flags & 0x80) == 0) return new IntPtr(1);
        }
        return CallNextHookEx(hook, code, message, data);
    }
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { internal uint Key, Scan, Flags, Time; internal UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private sealed class ActivityForm : Form
    {
        private readonly System.Windows.Forms.Timer clock = new() { Interval = 16 };
        private PointF from, target;
        private long moved, pulse;
        internal ActivityForm()
        {
            Text = "AGY control indicator"; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta; TopMost = true; DoubleBuffered = true;
            Native.GetPhysicalCursorPos(out var position); from = target = new(position.X, position.Y);
            clock.Tick += (_, _) =>
            {
                if (!Active) { Rest(); visible = false; if (Paused) CursorOverlay.Suspend(true); return; }
                if (Bounds != SystemInformation.VirtualScreen) Bounds = SystemInformation.VirtualScreen;
                if (!Visible) { Show(); visible = true; }
                var now = Environment.TickCount64;
                if (now - moved < 160 || pulse > 0 && now - pulse < 550) Invalidate();
            };
            clock.Start();
        }
        internal void Wake() { clock.Start(); if (!Visible) Show(); Invalidate(); }
        internal void Rest() { clock.Stop(); if (Visible) Hide(); }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } base.WndProc(ref m); }
        private PointF Position(long now) { var t = Math.Clamp((now - moved) / 110f, 0, 1); t = t * t * (3 - 2 * t); return new(from.X + (target.X - from.X) * t, from.Y + (target.Y - from.Y) * t); }
        internal void MovePointer(Point point, bool clicked) { var now = Environment.TickCount64; from = Position(now); target = point; moved = now; if (clicked) pulse = now; Invalidate(); }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var g = args.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TranslateTransform(-Left, -Top);
            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;
                using var glow = new Pen(Color.FromArgb(145, 191, 219), 9); using var edge = new Pen(Color.FromArgb(59, 130, 246), 4);
                g.DrawRectangle(glow, b.X + 4, b.Y + 4, b.Width - 9, b.Height - 9);
                g.DrawRectangle(edge, b.X + 2, b.Y + 2, b.Width - 5, b.Height - 5);
            }
            var primary = Screen.PrimaryScreen!.Bounds;
            var pill = new Rectangle(primary.X + (primary.Width - 330) / 2, primary.Y + 18, 330, 38);
            using var shape = new GraphicsPath(); shape.AddRoundedRectangle(pill, new Size(18, 18));
            using var background = new SolidBrush(Color.FromArgb(16, 43, 96)); g.FillPath(background, shape);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold); using var label = new SolidBrush(Color.FromArgb(239, 246, 255));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("AGY đang điều khiển  ·  ESC để dừng", font, label, pill, format);
            if (!CursorOverlay.HasEnabledProfile)
            {
                var now = Environment.TickCount64; var p = Position(now);
                using var blue = new Pen(Color.FromArgb(59, 130, 246), 2.5f); using var white = new SolidBrush(Color.White); using var halo = new SolidBrush(Color.FromArgb(191, 219, 254));
                g.FillEllipse(halo, p.X - 12, p.Y - 12, 30, 30);
                PointF[] arrow = [p, new(p.X + 5, p.Y + 25), new(p.X + 11, p.Y + 15), new(p.X + 24, p.Y + 11)];
                g.FillPolygon(white, arrow); g.DrawPolygon(blue, arrow);
                if (pulse > 0 && now - pulse < 500) { var radius = 16 + (now - pulse) / 14f; g.DrawEllipse(blue, p.X - radius, p.Y - radius, radius * 2, radius * 2); }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) clock.Dispose(); base.Dispose(disposing); }
    }
}
