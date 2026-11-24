using System.Diagnostics;
using System.Text.Json;
using AgyComputerUse;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        var browser = Path.GetFullPath(args[0]);
        var ui = args.Contains("--ui");
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var profile = Path.GetFullPath(Path.Combine(temporary, "agy-cdp-smoke-" + Guid.NewGuid().ToString("N")));
        if (Path.GetDirectoryName(profile) != temporary) throw new Exception("Test profile escaped its exact temporary parent.");
        Process? owned = null;
        using var desktop = new DesktopService();
        try
        {
            var began = DateTime.UtcNow;
            var browserArgs = new List<string> { "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "--force-renderer-accessibility", "about:blank" };
            if (!ui) browserArgs.Insert(0, "--headless=new");
            var reply = JsonSerializer.SerializeToElement(desktop.Dispatch("system", JsonSerializer.SerializeToElement(new { operation = "launch", app = browser, args = browserArgs, activate = ui, cdp = true, profile_dir = profile, timeout_ms = 8000 })), AgyComputerUse.Program.Json);
            Console.WriteLine(JsonSerializer.Serialize(new { verification = reply.GetProperty("verification").GetString(), reason = reply.TryGetProperty("reason", out var reason) ? reason.GetString() : null }));
            owned = Process.GetProcessById(reply.GetProperty("pid").GetInt32()); _ = owned.Handle;
            if (owned.StartTime.ToUniversalTime() < began || !string.Equals(owned.MainModule?.FileName, browser, StringComparison.OrdinalIgnoreCase)) throw new Exception("Fresh test browser identity was not proven.");
            if (reply.GetProperty("verification").GetString() != "passed") throw new Exception("Native launch did not verify the CDP endpoint.");
            var cdp = reply.GetProperty("cdp");
            var port = cdp.GetProperty("port").GetInt32();
            var sockets = Native.ListeningPort(port);
            if (sockets.Length == 0 || sockets.Any(item => !item.Loopback || !Native.IsDescendantProcess(item.Pid, owned.Id))) throw new Exception("CDP listener ownership or loopback binding changed.");
            Console.WriteLine("PASS real " + (ui ? "visible" : "headless") + " browser launches in a fresh profile, its loopback-only socket belongs to the launched process tree, and /json/version yields a valid debugger endpoint.");
            if (ui) BrowserUi.Run(desktop, reply);
            return 0;
        }
        finally
        {
            if (owned != null) { if (!owned.HasExited) { owned.Kill(true); owned.WaitForExit(5000); } owned.Dispose(); }
            // This exact nonce directory was named above before the browser launch.
            for (var retry = 0; Directory.Exists(profile) && retry < 20; retry++)
            {
                try { Directory.Delete(profile, true); }
                catch (IOException) when (retry < 19) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (retry < 19) { Thread.Sleep(100); }
            }
        }
    }
}
