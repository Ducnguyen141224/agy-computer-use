using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private object CreateProcessWatch(JsonElement args, string kind)
    {
        var app = args.Text("app", 2048);
        if (app != null && (app.Contains('\\') || app.Contains('/') || app.Contains('\0'))) throw new DesktopError("INVALID_ARGUMENT", "Process event app filter uses an executable name, not a path.");
        var query = args.Text("query");
        var selected = args.Text("window_id") is string id ? GetWindow(id) : null;
        if (app == null && selected == null) throw new DesktopError("INVALID_ARGUMENT", "Process watches need an executable app name or an existing window_id.");
        if (selected != null && kind == "app_launched") throw new DesktopError("INVALID_ARGUMENT", "app_launched watches future processes; omit an existing window_id.");
        var root = AutomationElement.RootElement;
        var watch = new EventWatch { Kind = kind, Root = root, Created = DateTime.UtcNow };
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        using var user = WindowsIdentity.GetCurrent(); var sid = user.User?.Value;
        object Fallback(string reason)
        {
            var monitor = new ProcessMonitor(watch, app, query, selected, session, sid);
            try { monitor.Start(); } catch { monitor.Dispose(); throw; }
            watch.NativeWatch = monitor;
            var fallbackToken = $"watch-{epoch}-{++sequence}"; watches[fallbackToken] = watch;
            return new { token = fallbackToken, @event = kind, expires_in_ms = 60000, armed = true, source = "same-user process generations sampled every 150 ms", fallback_reason = reason, note = "Termination checks pin the process generation. A process born and exited entirely between start samples may be missed." };
        }
        if (!IsElevated()) return Fallback("standard desktop integrity; kernel process trace is not requested");
        var source = new ManagementEventWatcher(new ManagementScope("root\\CIMV2"), new WqlEventQuery("SELECT * FROM " + (kind == "app_launched" ? "Win32_ProcessStartTrace" : "Win32_ProcessStopTrace") + " WHERE SessionID = " + session));
        var traceActive = 1;
        source.EventArrived += (_, info) =>
        {
            try
            {
                using var item = info.NewEvent;
                if (DateTime.UtcNow - watch.Created > TimeSpan.FromMinutes(1)) return;
                var name = Convert.ToString(item["ProcessName"]) ?? "";
                var pid = Convert.ToInt32(item["ProcessID"]);
                if (item["Sid"] is not byte[] bytes || new SecurityIdentifier(bytes, 0).Value != sid) return;
                if (selected != null && pid != selected.Pid) return;
                if (app != null && !System.IO.Path.GetFileNameWithoutExtension(name).Equals(System.IO.Path.GetFileNameWithoutExtension(app), StringComparison.OrdinalIgnoreCase)) return;
                if (query != null && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
                while (watch.Events.Count >= 20) watch.Events.TryDequeue(out _);
                watch.Events.Enqueue(new { @event = kind, app = Trim(name, 200), pid, timestamp = Convert.ToUInt64(item["TIME_CREATED"]).ToString(), source = "Windows WMI process trace" });
            }
            catch { /* A malformed provider event never escapes into WMI. */ }
        };
        source.Stopped += (_, info) => { if (Volatile.Read(ref traceActive) == 1 && info.Status != ManagementStatus.NoError) watch.Failure = "Windows stopped the process trace subscription: " + info.Status; };
        try { source.Start(); }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Interlocked.Exchange(ref traceActive, 0); source.Dispose(); watch.Failure = null;
            // Kernel trace subscriptions require rights that a normal desktop host may not have.
            // Keep the fallback explicit; never elevate the host to obtain an event subscription.
            return Fallback(error is ManagementException management ? management.ErrorCode.ToString() : error.GetType().Name);
        }
        watch.NativeWatch = source;
        var token = $"watch-{epoch}-{++sequence}"; watches[token] = watch;
        return new { token, @event = kind, expires_in_ms = 60000, armed = true, source = "Windows WMI process trace", scope = "current Windows user and logon session" };
    }
    private sealed class ProcessMonitor(EventWatch watch, string? app, string? query, WindowEntry? selected, int session, string? sid) : IDisposable
    {
        private readonly ManualResetEventSlim stop = new();
        private readonly Dictionary<string, Process> tracked = new();
        private Thread? worker;
        private void Sample(bool record)
        {
            foreach (var process in app == null ? new[] { Process.GetProcessById(selected!.Pid) } : Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(app)))
            {
                var retained = false;
                try
                {
                    if (process.SessionId != session || query != null && !process.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    var created = process.StartTime.ToUniversalTime().Ticks;
                    if (selected != null && (process.Id != selected.Pid || created != selected.Started)) continue;
                    var key = $"{process.Id}:{created}";
                    if (tracked.ContainsKey(key) || Native.ProcessUser(process.Id) != sid) continue;
                    _ = process.Handle;
                    tracked[key] = process; retained = true;
                    if (record && watch.Kind == "app_launched") Record(process);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { if (!retained) process.Dispose(); }
            }
        }
        private void Record(Process process)
        {
            while (watch.Events.Count >= 20) watch.Events.TryDequeue(out _);
            watch.Events.Enqueue(new { @event = watch.Kind, pid = process.Id, app = app ?? "selected process", source = "sampled process generation", note = "Observed lifecycle transition; processes shorter than the sampling interval may be missed." });
        }
        internal void Start()
        {
            Sample(false);
            worker = new Thread(() =>
            {
                try
                {
                    while (!stop.Wait(150) && DateTime.UtcNow - watch.Created < TimeSpan.FromMinutes(1))
                    {
                        foreach (var pair in tracked.ToArray())
                        {
                            if (!pair.Value.HasExited) continue;
                            if (watch.Kind == "app_terminated") Record(pair.Value);
                            tracked.Remove(pair.Key); pair.Value.Dispose();
                        }
                        // A selected process can be gone; its pinned generation above is sufficient.
                        if (selected == null) Sample(true);
                    }
                }
                catch { watch.Failure = "Process watch lost its generation or process enumeration access."; }
                finally { foreach (var process in tracked.Values) process.Dispose(); tracked.Clear(); }
            }) { IsBackground = true, Name = "AGY process lifecycle watch" };
            worker.Start();
        }
        public void Dispose() { stop.Set(); if (worker == null || worker.Join(500)) stop.Dispose(); }
    }
}
