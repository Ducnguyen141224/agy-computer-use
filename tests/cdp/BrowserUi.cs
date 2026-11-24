using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgyComputerUse;

internal static class BrowserUi
{
    internal static void Run(DesktopService desktop, JsonElement launch)
    {
        var window = launch.GetProperty("window").GetProperty("id").GetString()!;
        JsonElement Call(string method, object args) => JsonSerializer.SerializeToElement(desktop.Dispatch(method, JsonSerializer.SerializeToElement(args, AgyComputerUse.Program.Json)), AgyComputerUse.Program.Json);
        using var websocket = new ClientWebSocket();
        websocket.ConnectAsync(new Uri(launch.GetProperty("cdp").GetProperty("websocket_url").GetString()!), CancellationToken.None).GetAwaiter().GetResult();
        var html = "<!doctype html><title>AGY Browser Fixture</title><h1>Disposable browser fixture</h1><input aria-label='Browser message' value='initial'><button onclick=\"document.getElementById('sheet').hidden=false;document.getElementById('close').focus()\">Open sheet</button><div id='sheet' role='dialog' aria-modal='true' aria-label='Disposable sheet' hidden><p>Sheet body</p><button id='close' onclick=\"document.getElementById('sheet').hidden=true\">Close sheet</button></div><div role='alert' aria-label='Disposable alert'>Read this test alert</div><script>document.addEventListener('keydown',e=>{if(e.key==='Escape')document.getElementById('sheet').hidden=true})</script>";
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Target.createTarget", @params = new { url = "data:text/html," + Uri.EscapeDataString(html) } });
        websocket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        using var deadline = new CancellationTokenSource(5000);
        var buffer = new byte[65536]; var total = 0;
        do
        {
            var response = websocket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), deadline.Token).GetAwaiter().GetResult(); total += response.Count;
            if (!response.EndOfMessage) continue;
            using var message = JsonDocument.Parse(buffer.AsMemory(0, total));
            if (message.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1) { if (message.RootElement.TryGetProperty("error", out _)) throw new Exception("CDP fixture target creation failed."); break; }
            total = 0;
        } while (true);
        JsonElement Find(string name, string role)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var found = Call("find", new { window_id = window, name, role, depth = 20, budget_ms = 1000, limit = 10 });
                if (found.GetProperty("items").GetArrayLength() == 1) return found.GetProperty("items")[0];
                Thread.Sleep(100);
            } while (timer.ElapsedMilliseconds < 6000);
            throw new Exception("Browser UIA target was not unique: " + role + " " + name);
        }
        var input = Find("Browser message", "edit").GetProperty("ref").GetString();
        var set = Call("act", new { action = "set_value", @ref = input, text = "Browser UIA Unicode tiếng Việt", expect = new { property = "value", equals = "Browser UIA Unicode tiếng Việt" } });
        if (set.GetProperty("verification").GetString() != "passed") throw new Exception("Actual browser input value verification failed.");
        Console.WriteLine("PASS UIA3 finds and edits a real Chromium renderer control with Unicode and native readback.");
        var open = Find("Open sheet", "button").GetProperty("ref").GetString(); Call("act", new { action = "click", @ref = open });
        var sheet = Call("query", new { operation = "surface_wait", surface = "sheet", window_id = window, timeout_ms = 4000 });
        if (sheet.GetProperty("verification").GetString() != "passed") throw new Exception("Browser in-window sheet was not observed.");
        var item = sheet.GetProperty("surfaces")[0];
        var root = item.GetProperty("root_ref").GetString();
        var content = Call("observe", new { root_ref = root, interactive_only = false, depth = 8 });
        if (!content.GetProperty("items").EnumerateArray().Any(row => row.GetProperty("name").GetString() == "Close sheet")) throw new Exception("Sheet root could not be drilled.");
        Console.WriteLine("PASS a real Chromium ARIA dialog is discovered as a sheet and read through its guarded subtree ref.");
        var alerts = Call("system", new { operation = "list_surfaces", window_id = window, surface = "alert" });
        if (!alerts.GetProperty("surfaces").EnumerateArray().Any(row => row.GetProperty("name").GetString() == "Disposable alert")) throw new Exception("Browser ARIA alert was not recognized.");
        Call("system", new { operation = "close_surface", window_id = window, @ref = root });
        var dismissed = Call("query", new { operation = "surface_wait", window_id = window, surface = "sheet", present = false, timeout_ms = 4000 });
        if (dismissed.GetProperty("verification").GetString() != "passed") throw new Exception("Browser sheet dismissal was not proved.");
        Console.WriteLine("PASS Chromium alert discovery and actual Escape sheet dismissal are independently verified.");
        Call("act", new { action = "focus_window", window_id = window });
        Call("act", new { action = "right_click", @ref = input });
        var menu = Call("query", new { operation = "surface_wait", window_id = window, surface = "menu", timeout_ms = 3000 });
        if (menu.GetProperty("verification").GetString() != "passed") throw new Exception("Native Chromium context menu was not discovered.");
        var menuItem = menu.GetProperty("surfaces")[0];
        Call("system", new { operation = "close_surface", window_id = menuItem.GetProperty("window").GetProperty("id").GetString(), @ref = menuItem.TryGetProperty("root_ref", out var menuRoot) ? menuRoot.GetString() : null });
        var gone = Call("query", new { operation = "surface_wait", window_id = window, surface = "menu", present = false, timeout_ms = 3000 });
        if (gone.GetProperty("verification").GetString() != "passed") throw new Exception("Chromium context menu did not dismiss.");
        Console.WriteLine("PASS actual Chromium context menu is discovered and dismissed without choosing any clipboard action.");
    }
}
