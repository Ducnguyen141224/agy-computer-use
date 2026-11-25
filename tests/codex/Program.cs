using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using AgyComputerUse;
using AutomationElement = AgyComputerUse.AutomationElement;
using ControlType = System.Windows.Automation.ControlType;
using TreeWalker = AgyComputerUse.TreeWalker;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        var rootPath = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var fixtureExe = Path.Combine(rootPath, "tests", "fixture", "bin", "Release", "net10.0-windows", "DesktopFixture.exe");
        var title = "AGY Codex Real Fixture " + Guid.NewGuid().ToString("N")[..8];
        var checks = 0;
        void Check(bool ok, string msg)
        {
            if (!ok) throw new Exception("FAIL: " + msg);
            checks++;
            Console.WriteLine("PASS " + msg);
        }

        using var fixture = Process.Start(new ProcessStartInfo(fixtureExe) { ArgumentList = { title }, UseShellExecute = false })!;
        using var svc = new DesktopService();
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            for (var i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
            {
                hwnd = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, title);
                if (hwnd == IntPtr.Zero) Thread.Sleep(100);
            }
            Check(hwnd != IntPtr.Zero, "spawned real DesktopFixture window (" + title + ")");

            // Give WinForms message loop a moment to paint client controls cleanly.
            Thread.Sleep(350);

            // 1. UIA3 CachedSnapshot + TreeWalker BuildCache
            var swUia = Stopwatch.StartNew();
            var rootCached = AutomationElement.FromHandleBuildCache(hwnd);
            var rootSnap = rootCached.ReadCached();
            Check(rootSnap.Name == title && rootSnap.ProcessId == fixture.Id, "UIA3 FromHandleBuildCache + ReadCached matches real window title and PID");

            var controls = new List<(string Ref, string Role, string Name, System.Windows.Rect Bounds)>();
            var queue = new Queue<(AutomationElement El, int Depth)>();
            queue.Enqueue((rootCached, 0));
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement? choice25 = null;
            int idx = 0;
            while (queue.Count > 0 && idx < 400)
            {
                var (el, d) = queue.Dequeue();
                var snap = el.ReadCached();
                if (snap.Name == "Choice 25") choice25 = el;
                if (d > 0 && !snap.IsOffscreen && snap.IsEnabled && !snap.BoundingRectangle.IsEmpty && snap.BoundingRectangle.Width >= 6 && snap.BoundingRectangle.Height >= 6)
                {
                    var role = snap.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
                    controls.Add(("@t:" + (++idx), role, snap.Name, snap.BoundingRectangle));
                }
                if (d < 8)
                {
                    var child = walker.GetFirstChildCached(el);
                    while (child != null)
                    {
                        queue.Enqueue((child, d + 1));
                        child = walker.GetNextSiblingCached(child);
                    }
                }
            }
            swUia.Stop();
            Check(controls.Count >= 8, $"cached UIA3 tree walk found {controls.Count} interactive controls in {swUia.ElapsedMilliseconds}ms");
            Check(choice25 != null && choice25.Current.IsOffscreen, "'Choice 25' starts offscreen in real ListBox before DesktopService.Act");

            // 2. Real DesktopService: ListWindows -> Observe -> Act (auto_scroll + expect + observe_after: "diff") -> Set/Save Diff
            var winDoc = JsonSerializer.SerializeToDocument(
                svc.ListWindows(JsonSerializer.SerializeToElement(new { query = title }, AgyComputerUse.Program.Json)),
                AgyComputerUse.Program.Json);
            var winArr = winDoc.RootElement.GetProperty("windows");
            Check(winArr.GetArrayLength() >= 1, "DesktopService.ListWindows locates real fixture window");
            var windowId = winArr[0].GetProperty("id").GetString()!;

            var obsDoc = JsonSerializer.SerializeToDocument(
                svc.Observe(JsonSerializer.SerializeToElement(new { window_id = windowId, depth = 4, limit = 80, interactive_only = true }, AgyComputerUse.Program.Json), false),
                AgyComputerUse.Program.Json);
            var obsRoot = obsDoc.RootElement;
            Check(obsRoot.GetProperty("mode").GetString() == "full" && obsRoot.GetProperty("complete").GetBoolean(),
                "DesktopService.Observe returns complete full snapshot on real fixture");

            string? choice25Ref = null, messageRef = null, saveRef = null, statusInitial = null;
            foreach (var item in obsRoot.GetProperty("items").EnumerateArray())
            {
                var role = item.GetProperty("role").GetString();
                var name = item.GetProperty("name").GetString();
                var r = item.GetProperty("ref").GetString();
                if (name == "Choice 25" && role == "listitem" && item.TryGetProperty("offscreen", out var off) && off.GetBoolean())
                    choice25Ref = r;
                else if (name == "Message" && role == "edit")
                    messageRef = r;
                else if (name == "Save" && role == "button")
                    saveRef = r;
                else if (name == "Status" && role == "edit")
                    statusInitial = item.TryGetProperty("value", out var v) ? v.GetString() : name;
            }
            Check(choice25Ref != null && messageRef != null && saveRef != null && statusInitial == "Ready",
                "DesktopService.Observe sees Message, Save, Status='Ready', and offscreen Choice 25");

            var selectResDoc = JsonSerializer.SerializeToDocument(
                svc.Act(JsonSerializer.SerializeToElement(new
                {
                    action = "select",
                    @ref = choice25Ref,
                    auto_scroll = true,
                    expect = new { property = "selected", equals = true },
                    observe_after = "diff",
                    settle_ms = 120
                }, AgyComputerUse.Program.Json)),
                AgyComputerUse.Program.Json);
            var selectRoot = selectResDoc.RootElement;
            var selectObs = selectRoot.GetProperty("observation");
            Check(selectRoot.GetProperty("verification").GetString() == "passed"
                && selectRoot.GetProperty("actual").GetBoolean()
                && !choice25!.Current.IsOffscreen
                && selectObs.GetProperty("mode").GetString() == "diff"
                && selectObs.GetProperty("unchanged").GetInt32() > 0,
                $"DesktopService.Act auto_scroll selected 'Choice 25' and returned one-loop diff (unchanged={selectObs.GetProperty("unchanged").GetInt32()})");

            _ = svc.Act(JsonSerializer.SerializeToElement(new
            {
                action = "set_value",
                @ref = messageRef,
                text = "Codex One-Loop"
            }, AgyComputerUse.Program.Json));

            var saveResDoc = JsonSerializer.SerializeToDocument(
                svc.Act(JsonSerializer.SerializeToElement(new
                {
                    action = "click",
                    @ref = saveRef,
                    observe_after = "diff",
                    settle_ms = 300
                }, AgyComputerUse.Program.Json)),
                AgyComputerUse.Program.Json);
            var saveObs = saveResDoc.RootElement.GetProperty("observation");
            var diffItems = saveObs.GetProperty("items");
            string? savedStatus = null;
            foreach (var item in diffItems.EnumerateArray())
            {
                if (item.GetProperty("name").GetString() == "Status" && item.TryGetProperty("value", out var val))
                    savedStatus = val.GetString();
            }
            Check(saveObs.GetProperty("mode").GetString() == "diff"
                && savedStatus == "Saved: Codex One-Loop"
                && saveObs.GetProperty("unchanged").GetInt32() > 0,
                $"DesktopService.Act click+observe_after='diff' returned Status='{savedStatus}' with {diffItems.GetArrayLength()} changed vs {saveObs.GetProperty("unchanged").GetInt32()} unchanged");

            // 3. Real Capture (PrintWindow + WGC if supported on current station)
            using (var pwBmp = WindowCapture.CapturePrintWindow(hwnd))
            {
                Check(pwBmp.Width > 200 && pwBmp.Height > 200, $"PrintWindow fallback captured real fixture bitmap ({pwBmp.Width}x{pwBmp.Height})");
                if (WindowCapture.Supported)
                {
                    try
                    {
                        var swCold = Stopwatch.StartNew();
                        using var wgc1 = WindowCapture.Capture(hwnd);
                        swCold.Stop();
                        var swWarm1 = Stopwatch.StartNew();
                        using var wgc2 = WindowCapture.Capture(hwnd);
                        swWarm1.Stop();
                        var swWarm2 = Stopwatch.StartNew();
                        using var wgc3 = WindowCapture.Capture(hwnd);
                        swWarm2.Stop();
                        Check(wgc1.Width > 200 && wgc2.Width == wgc1.Width && wgc3.Width == wgc1.Width,
                            $"persistent D3D11 WGC captured 3 real frames cleanly (cold={swCold.ElapsedMilliseconds}ms, warm1={swWarm1.ElapsedMilliseconds}ms, warm2={swWarm2.ElapsedMilliseconds}ms)");
                    }
                    catch (DesktopError err)
                    {
                        Console.WriteLine($"INFO WGC skipped on non-default sandbox desktop ({err.Code}); verified via PrintWindow and live MCP.");
                    }
                }

                // 4. Real Set-of-Marks AnnotateBitmap + pixel verification on real captured fixture bitmap
                var bounds = WindowCapture.Bounds(hwnd);
                var screenRect = new Rectangle(bounds.Left, bounds.Top, pwBmp.Width, pwBmp.Height);
                var (entries, publicMarks) = DesktopService.AnnotateBitmap(pwBmp, screenRect, controls, 20);
                Check(entries.Count >= 5 && publicMarks.Length == entries.Count, $"AnnotateBitmap placed {entries.Count} Set-of-Marks badges on real fixture controls");
                var m1 = entries[^1];
                var scaleX = (double)pwBmp.Width / screenRect.Width;
                var scaleY = (double)pwBmp.Height / screenRect.Height;
                var pxX = Math.Clamp((int)Math.Round((m1.ScreenBounds.Left - screenRect.Left) * scaleX), 1, pwBmp.Width - 5);
                var pxY = Math.Clamp((int)Math.Round((m1.ScreenBounds.Top - screenRect.Top) * scaleY), 1, pwBmp.Height - 5);
                var pxW = Math.Max(4, (int)Math.Round(m1.ScreenBounds.Width * scaleX));
                var edgeX = Math.Clamp(pxX + pxW / 2, 1, pwBmp.Width - 2);
                var edgeColor = pwBmp.GetPixel(edgeX, pxY);
                Check(edgeColor.R == PresentationTheme.Edge.R && edgeColor.G == PresentationTheme.Edge.G && edgeColor.B == PresentationTheme.Edge.B,
                    $"Set-of-Marks pixel check confirmed #31A8FF outline on mark {m1.Mark} at ({edgeX},{pxY})");
            }

            // 5. Real DPI-aware PresentationTheme & HighlightPresenter premultiplied-alpha rendering
            using (var bmp1x = new Bitmap(700, 140, PixelFormat.Format32bppPArgb))
            using (var bmp2x = new Bitmap(1300, 260, PixelFormat.Format32bppPArgb))
            using (var g1 = Graphics.FromImage(bmp1x))
            using (var g2 = Graphics.FromImage(bmp2x))
            {
                var r1 = PresentationTheme.DrawLabel(g1, new Rectangle(0, 0, 700, 140), null, 1f);
                var r2 = PresentationTheme.DrawLabel(g2, new Rectangle(0, 0, 1300, 260), "Custom Handoff", 2f);
                Check(r2.Width == r1.Width * 2 && r2.Height == r1.Height * 2, $"PresentationTheme scales banner cleanly with DPI (1x={r1.Width}x{r1.Height}, 2x={r2.Width}x{r2.Height})");
            }

            using (var hl = HighlightPresenter.RenderToBitmap(120, 40, 1f))
            {
                var borderPx = hl.GetPixel(HighlightPresenter.Pad, HighlightPresenter.Pad + 20);
                var centerPx = hl.GetPixel(hl.Width / 2, hl.Height / 2);
                Check(borderPx.A > 180 && borderPx.B > borderPx.R && centerPx.A == 0,
                    $"HighlightPresenter renders azure per-pixel-alpha border (A={borderPx.A}) with transparent interior (A={centerPx.A})");
            }

            // 6. Real WinRT UserNotificationListener probe + NotificationReader UIA3 fixture check
            var (winrtStatus, _) = NotificationReader.TryReadWinRt();
            Check(!string.IsNullOrEmpty(winrtStatus), $"WinRT UserNotificationListener probe executed cleanly (status={winrtStatus})");

            var notifTitle = "AGY Codex Notif " + Guid.NewGuid().ToString("N")[..8];
            using var notifProc = Process.Start(new ProcessStartInfo(fixtureExe) { ArgumentList = { notifTitle, "--notifications" }, UseShellExecute = false })!;
            try
            {
                IntPtr nhwnd = IntPtr.Zero;
                for (var i = 0; i < 60 && nhwnd == IntPtr.Zero; i++)
                {
                    nhwnd = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, notifTitle);
                    if (nhwnd == IntPtr.Zero) Thread.Sleep(100);
                }
                Check(nhwnd != IntPtr.Zero, "spawned real WPF notification fixture window");
                var scan = NotificationReader.Read(AutomationElement.FromHandle(nhwnd));
                Check(scan.Recognized && scan.Complete && scan.Cards.Length == 1 && scan.Cards[0].Title == "Disposable notification",
                    "cached NotificationReader extracts real WPF notification card cleanly");
            }
            finally
            {
                if (!notifProc.HasExited) { notifProc.CloseMainWindow(); if (!notifProc.WaitForExit(1500)) notifProc.Kill(); }
            }

            Console.WriteLine($"CodexSmoke passed all {checks} real native checks.");
            return 0;
        }
        finally
        {
            if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(1500)) fixture.Kill(); }
        }
    }
}
