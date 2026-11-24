using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgyComputerUse;

// Presentation only: this window never receives focus or authorizes input.
internal static class CursorOverlay
{
    internal sealed record Profile(bool Enabled = false, string Style = "arrow", string Accent = "#2563EB", string Fill = "#FFFFFF", string Rim = "#172554", int Size = 26, string Label = "", int MaxWords = 6, bool Labels = true, int TravelMs = 120, int DwellMs = 1500, int HotspotX = 0, int HotspotY = 0, byte[]? Png = null, bool Ripple = true, bool Highlight = true, bool Caret = true,
        int? TravelMinMs = null, int? TravelMaxMs = null, double Bow = 0, double Overshoot = 0, double Tremor = 0, int MotionSeed = 0, int PauseMs = 0, byte[]? PointerPng = null, int PointerHotspotX = 0, int PointerHotspotY = 0);
    private sealed class Marker(Profile profile)
    {
        internal Profile Config = profile;
        internal PointF From, Target;
        internal long Moved, Pulse, Highlighted;
        internal Rectangle Highlight;
        internal Rectangle? Caret;
        internal Bitmap? Image, PointerImage;
        internal bool Pressable;
        internal int Duration, Moves;
        internal double Phase, Bend;
        internal void Load()
        {
            Image?.Dispose(); PointerImage?.Dispose(); Image = null; PointerImage = null;
            if (Config.Png != null) { using var data = new MemoryStream(Config.Png); using var bitmap = new Bitmap(data); Image = new Bitmap(bitmap); }
            if (Config.PointerPng != null) { using var data = new MemoryStream(Config.PointerPng); using var bitmap = new Bitmap(data); PointerImage = new Bitmap(bitmap); }
        }
        internal void Move(PointF target, long now)
        {
            From = Position(now); Target = target; Moved = now;
            var dx = Target.X - From.X; var dy = Target.Y - From.Y; var distance = Math.Sqrt(dx * dx + dy * dy);
            Duration = Config.TravelMinMs is int min && Config.TravelMaxMs is int max ? (int)Math.Round(min + (max - min) * Math.Min(1, distance / 1200)) : Config.TravelMs;
            var random = new Random(unchecked(Config.MotionSeed * 397 ^ ++Moves)); Phase = random.NextDouble() * Math.PI * 2; Bend = random.Next(2) == 0 ? -1 : 1;
        }
        internal PointF Position(long now)
        {
            var t = Duration == 0 ? 1 : Math.Clamp((now - Moved) / (double)Duration, 0, 1);
            if (t >= 1) return Target;
            static double Smooth(double value) => value * value * (3 - 2 * value);
            var eased = Config.Overshoot == 0 ? Smooth(t) : t <= .8 ? Smooth(t / .8) * (1 + Config.Overshoot) : 1 + Config.Overshoot * (1 - Smooth((t - .8) / .2));
            var dx = Target.X - From.X; var dy = Target.Y - From.Y; var distance = Math.Sqrt(dx * dx + dy * dy);
            var curve = Math.Sin(Math.PI * t) * Math.Min(80, distance * .12) * Config.Bow * Bend;
            var noise = Config.Tremor * Math.Sin(Math.PI * t) * Math.Sin(t * 20 + Phase);
            return new((float)(From.X + dx * eased - (distance == 0 ? 0 : dy / distance) * curve + noise), (float)(From.Y + dy * eased + (distance == 0 ? 0 : dx / distance) * curve + noise));
        }
    }
    private static OverlayForm? form;
    private static volatile bool enabled;
    private static bool suspended;
    internal static bool HasEnabledProfile => enabled;
    internal static void Suspend(bool value) { suspended = value; if (form != null) { if (value) form.Hide(); else if (enabled) form.Show(); } }
    private static string active = "default";
    internal static IntPtr WindowHandle;
    internal static object RenderState() => !enabled ? new { rendered = false, caret = (object?)null } : StaDispatcher.Run<object>(() => new { rendered = form?.Visible == true && WindowHandle != IntPtr.Zero, caret = form?.Markers.TryGetValue(active, out var marker) == true && marker.Caret is Rectangle r ? (object)new { x = r.X, y = r.Y, width = r.Width, height = r.Height } : null });
    internal static void SelectAgent(string selected) { active = selected; }
    internal static void Set(IReadOnlyDictionary<string, Profile> profiles, string selected) => StaDispatcher.Run(() =>
    {
        active = selected;
        enabled = profiles.Values.Any(item => item.Enabled);
        if (!enabled) { form?.Close(); form = null; WindowHandle = IntPtr.Zero; return true; }
        form ??= new OverlayForm();
        form.UpdateProfiles(profiles);
        if (!form.Visible && !suspended) form.Show();
        WindowHandle = form.Handle;
        return true;
    });
    internal static void Pointer(int x, int y, bool pulse = false)
    {
        ControlPresentation.Pointer(x, y, pulse);
        if (!enabled) return;
        StaDispatcher.Run(() => { if (form?.Markers.TryGetValue(active, out var marker) == true && marker.Config.Enabled) { var now = Environment.TickCount64; marker.Move(new(x, y), now); if (pulse) marker.Pulse = now; form.Invalidate(); } return true; });
    }
    internal static void Highlight(System.Windows.Rect rect, bool caret, bool pulse, bool pressable = false)
    {
        if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0) ControlPresentation.Pointer((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2), pulse);
        if (!enabled || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
        StaDispatcher.Run(() =>
        {
            if (form?.Markers.TryGetValue(active, out var marker) == true && marker.Config.Enabled)
            {
                var now = Environment.TickCount64;
                marker.Highlight = Rectangle.FromLTRB((int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom);
                marker.Highlighted = now; marker.Caret = null;
                marker.Pressable = pressable;
                marker.Move(new((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2)), now);
                if (pulse) marker.Pulse = now;
                form.Invalidate();
            }
            return true;
        });
    }
    internal static void WaitBeforeAction()
    {
        if (!enabled) return;
        var pause = StaDispatcher.Run(() => form?.Markers.TryGetValue(active, out var marker) == true && marker.Config.Enabled && marker.Config.PauseMs > 0 ? Math.Max(0, marker.Duration - (int)(Environment.TickCount64 - marker.Moved)) + marker.Config.PauseMs : 0);
        if (pause > 0) ControlPresentation.Delay(pause);
    }
    internal static void Caret(System.Windows.Rect? rect)
    {
        if (!enabled || rect == null) return;
        StaDispatcher.Run(() => { if (form?.Markers.TryGetValue(active, out var marker) == true) { var r = rect.Value; marker.Caret = new((int)r.X, (int)r.Y, Math.Max(2, (int)r.Width), (int)r.Height); marker.Highlighted = Environment.TickCount64; form.Invalidate(); } return true; });
    }
    internal static void Close()
    {
        if (form == null) return;
        enabled = false;
        StaDispatcher.Run(() => { form?.Close(); form = null; WindowHandle = IntPtr.Zero; return true; });
    }
    private sealed class OverlayForm : Form
    {
        internal readonly Dictionary<string, Marker> Markers = new();
        private readonly System.Windows.Forms.Timer clock = new() { Interval = 16 };
        private long lastPaint;
        internal OverlayForm()
        {
            Text = "AGY cursor presentation"; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta; TopMost = true; DoubleBuffered = true;
            clock.Tick += (_, _) =>
            {
                if (Bounds != SystemInformation.VirtualScreen) Bounds = SystemInformation.VirtualScreen;
                var now = Environment.TickCount64;
                var repaint = Markers.Values.Any(m => m.Config.Enabled &&
                    (now - m.Moved < m.Duration + 32 || lastPaint < m.Moved + m.Duration && now >= m.Moved + m.Duration || m.Pulse > 0 && now - m.Pulse < 550 ||
                     m.Highlighted > 0 && lastPaint < m.Highlighted + m.Config.DwellMs && now >= m.Highlighted + m.Config.DwellMs ||
                     m.Caret != null && now - m.Highlighted < m.Config.DwellMs && now / 450 != lastPaint / 450));
                if (repaint) Invalidate();
            };
            clock.Start();
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; } }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } base.WndProc(ref m); }
        internal void UpdateProfiles(IReadOnlyDictionary<string, Profile> profiles)
        {
            foreach (var key in Markers.Keys.Except(profiles.Keys).ToArray()) { Markers[key].Image?.Dispose(); Markers[key].PointerImage?.Dispose(); Markers.Remove(key); }
            Native.GetPhysicalCursorPos(out var point);
            foreach (var pair in profiles)
            {
                if (!Markers.TryGetValue(pair.Key, out var marker)) Markers[pair.Key] = marker = new(pair.Value) { From = new(point.X, point.Y), Target = new(point.X, point.Y) };
                marker.Config = pair.Value; marker.Load();
            }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args); var g = args.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(-Left, -Top); var now = Environment.TickCount64;
            lastPaint = now;
            foreach (var marker in Markers.Values.Where(item => item.Config.Enabled))
            {
                var c = marker.Config; var p = marker.Position(now); var s = c.Size;
                using var accent = new Pen(ColorTranslator.FromHtml(c.Accent), 3);
                using var rim = new Pen(ColorTranslator.FromHtml(c.Rim), 2);
                using var fill = new SolidBrush(ColorTranslator.FromHtml(c.Fill));
                if (now - marker.Highlighted < c.DwellMs)
                {
                    var rect = marker.Highlight; if (c.Highlight) g.DrawRectangle(accent, rect);
                    if (c.Caret && marker.Caret is Rectangle caret && (now / 450) % 2 == 0) g.DrawRectangle(accent, caret);
                }
                if (c.Ripple && now - marker.Pulse is >= 0 and < 500 && marker.Pulse > 0)
                {
                    var radius = s * (0.6f + (now - marker.Pulse) / 350f); g.DrawEllipse(accent, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                }
                var custom = marker.Pressable && marker.PointerImage != null ? marker.PointerImage : marker.Image;
                if (custom != null) g.DrawImage(custom, p.X - (marker.Pressable && marker.PointerImage != null ? c.PointerHotspotX : c.HotspotX), p.Y - (marker.Pressable && marker.PointerImage != null ? c.PointerHotspotY : c.HotspotY), s, s);
                else if (c.Style == "arrow")
                {
                    PointF[] points = [p, new(p.X + s * .22f, p.Y + s), new(p.X + s * .45f, p.Y + s * .58f), new(p.X + s, p.Y + s * .45f)];
                    g.FillPolygon(fill, points); g.DrawPolygon(rim, points); g.DrawLine(accent, points[1], points[2]);
                }
                else if (c.Style == "crosshair") { g.DrawLine(accent, p.X - s / 2, p.Y, p.X + s / 2, p.Y); g.DrawLine(accent, p.X, p.Y - s / 2, p.X, p.Y + s / 2); }
                else { if (c.Style == "dot") g.FillEllipse(fill, p.X - s / 2, p.Y - s / 2, s, s); g.DrawEllipse(accent, p.X - s / 2, p.Y - s / 2, s, s); }
                if (c.Labels && c.Label.Length > 0)
                {
                    var label = string.Join(' ', c.Label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(c.MaxWords));
                    var at = new Point((int)p.X + s + 6, (int)p.Y + 6);
                    TextRenderer.DrawText(g, label, SystemFonts.MessageBoxFont, at, ColorTranslator.FromHtml(c.Rim), ColorTranslator.FromHtml(c.Fill));
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { clock.Dispose(); foreach (var marker in Markers.Values) { marker.Image?.Dispose(); marker.PointerImage?.Dispose(); } Markers.Clear(); } base.Dispose(disposing); }
    }
}
