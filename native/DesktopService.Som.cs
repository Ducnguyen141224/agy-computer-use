using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    internal sealed record MarkEntry(int Mark, int ScreenX, int ScreenY, Rectangle ScreenBounds, string Ref, string Role, string Name);

    internal static (List<MarkEntry> Entries, object[] PublicMarks) AnnotateBitmap(
        Bitmap bitmap,
        Rectangle sourceScreenRect,
        IReadOnlyList<(string Ref, string Role, string Name, System.Windows.Rect Bounds)> controls,
        int maxMarks)
    {
        var entries = new List<MarkEntry>();
        var publicMarks = new List<object>();
        if (sourceScreenRect.Width <= 0 || sourceScreenRect.Height <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0)
            return (entries, []);

        var scaleX = (double)bitmap.Width / sourceScreenRect.Width;
        var scaleY = (double)bitmap.Height / sourceScreenRect.Height;
        var usedCenters = new List<(int X, int Y)>();

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var boxPen = new Pen(PresentationTheme.Edge, 2f);
        using var badgeBrush = new SolidBrush(PresentationTheme.LabelBottom);
        using var badgeRim = new Pen(PresentationTheme.Ice, 1.2f);
        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        foreach (var ctrl in controls)
        {
            if (entries.Count >= maxMarks) break;
            if (ctrl.Bounds.IsEmpty || ctrl.Bounds.Width < 6 || ctrl.Bounds.Height < 6) continue;
            var screenBox = Rectangle.FromLTRB(
                (int)Math.Floor(ctrl.Bounds.Left),
                (int)Math.Floor(ctrl.Bounds.Top),
                (int)Math.Ceiling(ctrl.Bounds.Right),
                (int)Math.Ceiling(ctrl.Bounds.Bottom));
            var clipped = Rectangle.Intersect(screenBox, sourceScreenRect);
            if (clipped.Width < 6 || clipped.Height < 6) continue;

            var centerX = clipped.Left + clipped.Width / 2;
            var centerY = clipped.Top + clipped.Height / 2;
            if (usedCenters.Any(pt => Math.Abs(pt.X - centerX) <= 5 && Math.Abs(pt.Y - centerY) <= 5)) continue;
            usedCenters.Add((centerX, centerY));

            var mark = entries.Count + 1;
            entries.Add(new(mark, centerX, centerY, clipped, ctrl.Ref, ctrl.Role, ctrl.Name));
            publicMarks.Add(new { mark, role = ctrl.Role, name = ctrl.Name, @ref = ctrl.Ref });

            var ix = (int)Math.Round((clipped.Left - sourceScreenRect.Left) * scaleX);
            var iy = (int)Math.Round((clipped.Top - sourceScreenRect.Top) * scaleY);
            var iw = Math.Max(4, (int)Math.Round(clipped.Width * scaleX));
            var ih = Math.Max(4, (int)Math.Round(clipped.Height * scaleY));
            var rect = new Rectangle(
                Math.Clamp(ix, 1, Math.Max(1, bitmap.Width - 5)),
                Math.Clamp(iy, 1, Math.Max(1, bitmap.Height - 5)),
                Math.Min(iw, Math.Max(4, bitmap.Width - Math.Clamp(ix, 1, Math.Max(1, bitmap.Width - 5)) - 1)),
                Math.Min(ih, Math.Max(4, bitmap.Height - Math.Clamp(iy, 1, Math.Max(1, bitmap.Height - 5)) - 1)));
            g.SmoothingMode = SmoothingMode.None;
            g.DrawRectangle(boxPen, rect);

            var label = mark.ToString();
            var badgeW = label.Length > 1 ? 22 : 17;
            var badgeH = 16;
            var bx = Math.Clamp(rect.Left, 1, Math.Max(1, bitmap.Width - badgeW - 1));
            var by = Math.Clamp(rect.Top >= badgeH + 1 ? rect.Top - badgeH : rect.Top + 1, 1, Math.Max(1, bitmap.Height - badgeH - 1));
            var badge = new Rectangle(bx, by, badgeW, badgeH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(badge, new Size(4, 4));
            g.FillPath(badgeBrush, path);
            g.DrawPath(badgeRim, path);
            g.DrawString(label, font, textBrush, badge, fmt);
        }

        return (entries, publicMarks.ToArray());
    }

    private (List<MarkEntry> Entries, object[] PublicMarks) CollectAndAnnotateMarks(Bitmap bitmap, WindowEntry window, Rectangle sourceScreenRect, int maxMarks)
    {
        var collected = new List<(string Ref, string Role, string Name, System.Windows.Rect Bounds)>();
        var root = AutomationElement.FromHandleBuildCache(window.Handle);
        var queue = new Queue<(AutomationElement Element, int Depth, AncestryChain? Chain)>();
        queue.Enqueue((root, 0, null));
        var walker = TreeWalker.ControlViewWalker;
        var visited = 0;
        var sw = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 700 && sw.ElapsedMilliseconds < 1200 && collected.Count < maxMarks * 2)
        {
            var (element, level, chain) = queue.Dequeue();
            try
            {
                visited++;
                var snap = element.ReadCached();
                if (snap.IsOffscreen) continue;
                var isInteractive = IsInteractive(snap.ControlType);
                if (level > 0 && isInteractive && snap.IsEnabled && !snap.BoundingRectangle.IsEmpty && snap.BoundingRectangle.Width >= 6 && snap.BoundingRectangle.Height >= 6)
                {
                    var box = Rectangle.FromLTRB((int)snap.BoundingRectangle.Left, (int)snap.BoundingRectangle.Top, (int)snap.BoundingRectangle.Right, (int)snap.BoundingRectangle.Bottom);
                    if (box.IntersectsWith(sourceScreenRect))
                    {
                        var reference = AddRef(window, element, snap, FormatChain(chain));
                        var role = snap.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                        collected.Add((reference, role, Trim(snap.Name, 120), snap.BoundingRectangle));
                    }
                }
                if (level >= 12) continue;
                var selfPart = $"{snap.RuntimeId}:{snap.ControlTypeId}:{snap.AutomationId}:{snap.Name}";
                var childChain = snap.NativeWindowHandle == window.Handle.ToInt64()
                    ? new AncestryChain(selfPart, null)
                    : new AncestryChain(selfPart, level == 0 && chain == null ? AncestryChainFrom(element, window) : chain);
                var child = walker.GetFirstChildCached(element);
                while (child != null && queue.Count + visited < 700 && sw.ElapsedMilliseconds < 1200)
                {
                    queue.Enqueue((child, level + 1, childChain));
                    child = walker.GetNextSiblingCached(child);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (DesktopError) { }
        }

        return AnnotateBitmap(bitmap, sourceScreenRect, collected, maxMarks);
    }

    private void ClickMark(JsonElement args, WindowEntry window)
    {
        var captureId = args.Text("capture_id") ?? throw new DesktopError("INVALID_ARGUMENT", "capture_id is required for click_mark.");
        var markNum = args.Number("mark", -1, 1, 60);
        if (!captures.TryGetValue(captureId, out var capture) || capture.Window != null && capture.Window.Id != window.Id || DateTime.UtcNow - capture.Created > TimeSpan.FromSeconds(10))
            throw new DesktopError("STALE_CAPTURE", "Capture is missing, expired, used, or belongs to another window. Capture again.");
        if (capture.Marks == null || capture.Marks.Count == 0)
            throw new DesktopError("INVALID_ARGUMENT", "Capture has no Set-of-Marks annotations. Call desktop_capture with annotate=true.");
        EnsureForeground(window);
        if (!Native.GetWindowRect(window.Handle, out var current)) throw new DesktopError("WINDOW_CHANGED", "Window geometry is unavailable.");
        if (!windowGeometry.TryGetValue(captureId, out var original) || !SameRect(current, original))
            throw new DesktopError("WINDOW_CHANGED", "Window moved or resized after capture. Capture again.");
        var mark = capture.Marks.FirstOrDefault(m => m.Mark == markNum) ?? throw new DesktopError("INVALID_ARGUMENT", $"Mark {markNum} does not exist in this capture.");
        var button = args.Text("button") ?? "left";
        var count = args.Number("count", 1, 1, 3);
        if (button is not ("left" or "right" or "middle")) throw new DesktopError("INVALID_ARGUMENT", "Invalid mouse button.");
        var point = new Native.Point(mark.ScreenX, mark.ScreenY);
        CheckPoint(window, point, true);
        ControlPresentation.FlashHighlight(mark.ScreenBounds);
        ConsumeCapture(captureId);
        Native.WithPointerModifiers(args, () => Native.ClickAt(point.X, point.Y, button, count));
    }
}
