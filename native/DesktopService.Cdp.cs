using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private static int ChooseCdpPort(int requested)
    {
        var listener = new TcpListener(IPAddress.Loopback, requested);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        catch (SocketException) { throw new DesktopError("CDP_PORT_IN_USE", "The requested loopback port is unavailable. No process was launched."); }
        finally { listener.Stop(); }
    }
    private object VerifyCdp(Native.LaunchedProcess process, int port, JsonElement args)
    {
        _ = process.Handle; // Keep this process generation alive while checking ownership.
        var started = process.StartTime.ToUniversalTime();
        var timer = Stopwatch.StartNew();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(700), MaxResponseContentBufferSize = 65536 };
        var endpoint = $"http://127.0.0.1:{port}";
        do
        {
            var listeners = Native.ListeningPort(port);
            if (listeners.Any(item => !item.Loopback)) return new { delivery = "sent", pid = process.Id, verification = "failed", reason = "Debug endpoint was not loopback-only. No endpoint was returned." };
            if (listeners.Length > 0 && listeners.All(item => Native.IsDescendantProcess(item.Pid, process.Id)))
            {
                try
                {
                    using var owner = Process.GetProcessById(listeners[0].Pid);
                    if (owner.StartTime.ToUniversalTime() < started) throw new InvalidOperationException();
                    using var document = JsonDocument.Parse(http.GetStringAsync(endpoint + "/json/version").GetAwaiter().GetResult());
                    var data = document.RootElement;
                    var address = data.GetProperty("webSocketDebuggerUrl").GetString();
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var websocket) || websocket.Scheme != "ws" || !websocket.IsLoopback || websocket.Port != port || !websocket.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal)) throw new InvalidOperationException();
                    var product = data.GetProperty("Browser").GetString() ?? "";
                    if (product.Length > 256) throw new InvalidOperationException();
                    object? activated = null;
                    if (args.Flag("activate"))
                    {
                        _ = ListWindows(JsonSerializer.SerializeToElement(new { limit = 60 }));
                        var candidate = windows.Values.Where(entry => ValidWindow(entry) && Native.IsWindowVisible(entry.Handle) && Native.IsDescendantProcess(entry.Pid, process.Id)).ToArray();
                        if (candidate.Length != 1 || !Native.ActivateWindow(candidate[0].Handle)) { Thread.Sleep(80); continue; }
                        activated = WindowInfo(candidate[0]);
                    }
                    return new { delivery = "sent", pid = process.Id, verification = "passed", cdp = new { http_endpoint = endpoint, port, product, websocket_url = address }, window = activated, profile_dir = args.Text("profile_dir", 2048), note = "Fresh profile and a loopback endpoint owned by the launched process tree were verified. The caller owns the profile directory and browser lifetime." };
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or KeyNotFoundException) { }
            }
            if (process.HasExited && listeners.Length == 0) break;
            Thread.Sleep(80);
        } while (timer.ElapsedMilliseconds < args.Number("timeout_ms", 6000, 100, 8000));
        return new { delivery = "sent", pid = process.Id, verification = "failed", reason = "No owned Chromium debug endpoint was verified before timeout. Inspect the launched process before retrying." };
    }
}
