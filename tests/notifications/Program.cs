using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using AgyComputerUse;
using AutomationElement = AgyComputerUse.AutomationElement;
using InvokePattern = AgyComputerUse.InvokePattern;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--shell"))
        {
            var channel = Array.IndexOf(args, "--result-pipe");
            if (channel >= 0) return IndependentFixture.Report(args[channel + 1], args);
            return Native.PackageIdentity() != null ? IndependentFixture.Run() : LiveNotificationSmoke.Run(args);
        }
        var rootPath = Path.GetFullPath(args[0]);
        var title = "AGY Notification Parser " + Guid.NewGuid().ToString("N");
        using var fixture = Process.Start(new ProcessStartInfo(Path.Combine(rootPath, "tests/fixture/bin/Release/net10.0-windows/DesktopFixture.exe")) { ArgumentList = { title, "--notifications" }, UseShellExecute = false })!;
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        try
        {
            AutomationElement? root = null;
            for (var i = 0; i < 50 && root == null; i++) { var handle = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, title); if (handle != IntPtr.Zero) root = AutomationElement.FromHandle(handle); else Thread.Sleep(100); }
            if (root == null) throw new Exception("Test fixture did not open.");
            using var service = new DesktopService();
            var windows = JsonSerializer.SerializeToElement(service.Dispatch("windows", JsonSerializer.SerializeToElement(new { query = title })), AgyComputerUse.Program.Json);
            var id = windows.GetProperty("windows")[0].GetProperty("id").GetString();
            var tree = JsonSerializer.SerializeToElement(service.Dispatch("observe", JsonSerializer.SerializeToElement(new { window_id = id, depth = 5, limit = 60, interactive_only = false })), AgyComputerUse.Program.Json);
            Check(tree.GetProperty("items").GetArrayLength() > 2, "cold UIA3 engine observes the actual notification fixture controls");
            var scan = NotificationReader.Read(root);
            Check(scan.Recognized && scan.Complete, "recognizes the live WPF notification contract without focusing the window");
            Check(scan.Cards.Length == 1, "excludes ordinary calendar/list rows");
            var card = scan.Cards.Single();
            Check(card.Title == "Disposable notification" && card.App == "AGY Test App" && card.Body == "Disposable body", "extracts separate app, title and body from actual UIA nodes");
            Check(card.Actions.Length == 1 && card.Dismiss != null, "separates named action buttons from dismiss and clear-all");
            var identity = card.Identity;
            if (!card.Actions[0].TryGetCurrentPattern(InvokePattern.Pattern, out var action)) throw new Exception("Missing action pattern.");
            ((InvokePattern)action).Invoke();
            for (var i = 0; i < 20 && NotificationReader.Read(root).Cards.Single().Identity == identity; i++) Thread.Sleep(50);
            Check(NotificationReader.Read(root).Cards.Single().Identity != identity, "identity changes when a live notification title changes");
            var refused = false;
            try { service.Dispatch("system", JsonSerializer.SerializeToElement(new { operation = "list_notifications", window_id = id })); } catch (DesktopError error) when (error.Code == "SURFACE_NOT_FOUND") { refused = true; }
            Check(refused, "production engine refuses a notification-shaped ordinary application");
            if (!card.Dismiss!.TryGetCurrentPattern(InvokePattern.Pattern, out var dismiss)) throw new Exception("Missing dismiss pattern.");
            ((InvokePattern)dismiss).Invoke();
            for (var i = 0; i < 20 && NotificationReader.Read(root).Cards.Length != 0; i++) Thread.Sleep(50);
            Check(NotificationReader.Read(root) is { Complete: true, Recognized: true, Cards.Length: 0 }, "reads back actual removal of the disposable card");
            Console.WriteLine($"Notification parser passed: {checks} checks. This does not verify the Windows shell notification center.");
            return 0;
        }
        finally { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) fixture.Kill(); } }
    }
}
